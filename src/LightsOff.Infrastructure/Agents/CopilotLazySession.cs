using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using LightsOff.Core.Abstractions;
using LightsOff.Core.Domain;
using Microsoft.Extensions.Logging;

namespace LightsOff.Infrastructure.Agents;

/// <summary>
/// Copilot CLI doesn't take stdin streaming the way Claude does — its prompt arrives via the
/// <c>-p</c> flag at process spawn. So this session is "lazy": <c>StartSessionAsync</c> just
/// captures the spec + base prompt; the actual <c>copilot</c> process is spawned on the first
/// <see cref="SendAsync"/> call, with the input's instruction appended to the base prompt.
///
/// Subsequent SendAsync calls (multi-turn chat during implementation) are a no-op for now —
/// supporting them would require <c>copilot --resume &lt;id&gt;</c> with a fresh process per turn,
/// and bridging that back into the same event stream. Future work.
/// </summary>
public sealed class CopilotLazySession : IAgentSession
{
    private readonly string _copilotPath;
    private readonly AgentSessionSpec _spec;
    private readonly string _basePrompt;
    private readonly ILogger _logger;
    private readonly Channel<AgentEvent> _events;
    private readonly object _sync = new();
    private SubprocessAgentSession? _inner;
    private Task? _forwarder;
    private bool _spawned;
    private bool _disposed;

    public string SessionId { get; }
    public string AgentId => CopilotCliAgent.AgentId;
    public IAsyncEnumerable<AgentEvent> Events => _events.Reader.ReadAllAsync();

    public CopilotLazySession(string copilotPath, AgentSessionSpec spec, string basePrompt, ILogger logger)
    {
        _copilotPath = copilotPath;
        _spec = spec;
        _basePrompt = basePrompt;
        _logger = logger;
        SessionId = Guid.NewGuid().ToString("N");
        _events = Channel.CreateUnbounded<AgentEvent>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false
        });
    }

    public async Task SendAsync(AgentInput input, CancellationToken ct = default)
    {
        // First SendAsync spawns the process. Subsequent calls are no-ops (multi-turn TODO).
        bool needsSpawn;
        lock (_sync)
        {
            needsSpawn = !_spawned;
            if (needsSpawn) _spawned = true;
        }
        if (!needsSpawn)
        {
            _logger.LogDebug("copilot session {SessionId}: ignoring follow-up SendAsync (multi-turn not supported yet)", SessionId);
            return;
        }

        var instruction = ExtractInstruction(input);
        var fullPrompt = string.IsNullOrWhiteSpace(instruction)
            ? _basePrompt
            : _basePrompt + "\n\n" + instruction;

        var cwd = _spec.AllowedRepoPaths[0];
        var psi = new ProcessStartInfo
        {
            FileName = _copilotPath,
            WorkingDirectory = cwd,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(fullPrompt);
        psi.ArgumentList.Add("--output-format"); psi.ArgumentList.Add("json");
        psi.ArgumentList.Add("--no-ask-user");
        psi.ArgumentList.Add("--allow-all-tools");
        psi.ArgumentList.Add("--no-color");
        foreach (var extra in _spec.AllowedRepoPaths.Skip(1))
        {
            psi.ArgumentList.Add("--add-dir");
            psi.ArgumentList.Add(extra);
        }

        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start copilot process.");
        try { proc.StandardInput.Close(); } catch { }

        _logger.LogInformation(
            "copilot-cli lazy session {SessionId} spawned (pid={Pid}, cwd={Cwd}, ticket={Ticket}, mode={Mode})",
            SessionId, proc.Id, cwd, _spec.TicketKey, _spec.Mode);

        _inner = new SubprocessAgentSession(
            SessionId, CopilotCliAgent.AgentId, proc, _logger,
            parseLine: CopilotJsonlParser.Parse,
            serializeInput: null);

        // Forward events from inner session to our channel.
        _forwarder = Task.Run(async () =>
        {
            try
            {
                await foreach (var ev in _inner.Events.WithCancellation(ct).ConfigureAwait(false))
                {
                    await _events.Writer.WriteAsync(ev, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "copilot session {SessionId} event forwarder errored", SessionId);
            }
            finally
            {
                _events.Writer.TryComplete();
            }
        });

        await Task.CompletedTask;
    }

    public Task PauseAsync(CancellationToken ct = default) =>
        _inner?.PauseAsync(ct) ?? Task.CompletedTask;

    public Task ResumeAsync(CancellationToken ct = default) =>
        _inner?.ResumeAsync(ct) ?? Task.CompletedTask;

    public async Task CancelAsync(CancellationToken ct = default)
    {
        if (_inner is not null)
            await _inner.CancelAsync(ct).ConfigureAwait(false);
        else
            // Never spawned — just complete the channel so callers awaiting Events unblock.
            _events.Writer.TryComplete();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try { _events.Writer.TryComplete(); } catch { }
        if (_inner is not null) await _inner.DisposeAsync().ConfigureAwait(false);
        if (_forwarder is not null)
        {
            await Task.WhenAny(_forwarder, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
        }
    }

    private static string ExtractInstruction(AgentInput input) => input switch
    {
        AgentTaskInput t   => t.Instruction,
        AgentChatInput c   => c.Message,
        AgentAnswerInput a => $"Answer to clarifying question {a.QuestionId}: {a.Answer}",
        _ => string.Empty
    };
}
