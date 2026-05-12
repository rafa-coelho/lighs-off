using System.Text.Json;
using System.Threading.Channels;
using LightsOff.Core.Abstractions;
using LightsOff.Core.Domain;

namespace LightsOff.Infrastructure.Demo;

/// <summary>
/// Scripted agent session. Replays a deterministic sequence of agent events for the live demo —
/// log chunks, tool calls, a streaming chat reply, and a terminal SessionFinishedEvent. Honors
/// <see cref="CancelAsync"/> so the user-cancel path works.
///
/// Note: not used by <see cref="DemoPipelineOrchestrator"/> (which emits events directly), but
/// the contract is required by <see cref="ICodeAgent.StartSessionAsync"/> and any UI surface that
/// asks for a session keeps working.
/// </summary>
internal sealed class DemoAgentSession : IAgentSession
{
    private static readonly TimeSpan StepDelay = TimeSpan.FromMilliseconds(220);
    private readonly Channel<AgentEvent> _channel;
    private readonly CancellationTokenSource _cts;

    public string SessionId { get; } = Guid.NewGuid().ToString("N");
    public string AgentId => DemoCodeAgent.AgentIdConst;
    public IAsyncEnumerable<AgentEvent> Events => _channel.Reader.ReadAllAsync();

    public DemoAgentSession(AgentSessionSpec spec)
    {
        _channel = Channel.CreateUnbounded<AgentEvent>();
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => RunScriptAsync(spec, _cts.Token));
    }

    public Task SendAsync(AgentInput input, CancellationToken ct = default) => Task.CompletedTask;
    public Task PauseAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task ResumeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task CancelAsync(CancellationToken ct = default)
    {
        _cts.Cancel();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _channel.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    private async Task RunScriptAsync(AgentSessionSpec spec, CancellationToken ct)
    {
        try
        {
            var script = ScriptFor(spec.Mode);
            foreach (var ev in script)
            {
                if (ct.IsCancellationRequested) break;
                await Task.Delay(StepDelay, ct).ConfigureAwait(false);
                await _channel.Writer.WriteAsync(ev with { }, ct).ConfigureAwait(false);
            }
            if (!ct.IsCancellationRequested)
            {
                await _channel.Writer.WriteAsync(
                    new SessionFinishedEvent(DateTimeOffset.UtcNow, Success: true, Error: null), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            try
            {
                await _channel.Writer.WriteAsync(
                    new SessionFinishedEvent(DateTimeOffset.UtcNow, Success: false, Error: "Cancelled")).ConfigureAwait(false);
            }
            catch { /* channel may already be completed */ }
        }
        finally
        {
            _channel.Writer.TryComplete();
        }
    }

    private static IReadOnlyList<AgentEvent> ScriptFor(AgentSessionMode mode) => mode switch
    {
        AgentSessionMode.Implement => ImplementScript(),
        AgentSessionMode.Validate => ValidateScript(),
        AgentSessionMode.Review => ReviewScript(),
        AgentSessionMode.PlanChat => PlanChatScript(),
        _ => ImplementScript(),
    };

    private static List<AgentEvent> ImplementScript() => new()
    {
        Log("stdout", "Reading approved plan and ticket context…\n"),
        Tool("Read",  new { path = "src/Api/Program.cs" }),
        Log("stdout", "Found middleware registration order — Routing is set after Auth.\n"),
        Tool("Read",  new { path = "src/Api/appsettings.json" }),
        Tool("Write", new { path = "src/Api/Configuration/RateLimitOptions.cs" }),
        Tool("Write", new { path = "src/Api/Middleware/RateLimitMiddleware.cs" }),
        Tool("Edit",  new { path = "src/Api/Program.cs" }),
        Tool("Edit",  new { path = "src/Api/appsettings.json" }),
        Tool("Write", new { path = "tests/Api.Tests/RateLimitTests.cs" }),
        Tool("Bash",  new { command = "dotnet build" }),
        Log("stdout", "Build succeeded.\n    0 Error(s)\n    0 Warning(s)\n"),
        Chat("Implementation complete. Added a token-bucket `RateLimitMiddleware`, bound it to `RateLimit:RequestsPerMinute` in appsettings (default 60), and registered it before routing in Program.cs. Returns 429 with `Retry-After` when the bucket is empty. Tests cover happy path, exhausted bucket, and the header.", isFinal: true),
    };

    private static List<AgentEvent> ValidateScript() => new()
    {
        Log("stdout", "Re-running tests after auto-fix…\n"),
        Tool("Bash", new { command = "dotnet test" }),
        Log("stdout", "Passed!  - Failed: 0, Passed: 26, Skipped: 0, Total: 26, Duration: 1 s\n"),
        Chat("All tests pass.", isFinal: true),
    };

    private static List<AgentEvent> ReviewScript() => new()
    {
        Chat("Diff looks good. No security or quality concerns.", isFinal: true),
    };

    private static List<AgentEvent> PlanChatScript() => new()
    {
        Chat("Updated the plan to reflect your feedback.", isFinal: true),
    };

    private static LogChunkEvent Log(string stream, string text) =>
        new(DateTimeOffset.UtcNow, stream, text);

    private static ToolCallEvent Tool(string name, object args) =>
        new(DateTimeOffset.UtcNow, name, JsonSerializer.Serialize(args));

    private static ChatReplyEvent Chat(string text, bool isFinal) =>
        new(DateTimeOffset.UtcNow, text, isFinal);
}
