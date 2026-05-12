using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using LightsOff.Core.Abstractions;
using LightsOff.Core.Domain;
using Microsoft.Extensions.Logging;

namespace LightsOff.Infrastructure.Agents;

/// <summary>
/// Host-side streaming agent session. Wraps a child process whose stdout emits JSONL events,
/// bridges them through a <see cref="Channel{AgentEvent}"/>, and exposes cancellation via POSIX
/// signals. Agent-specific framing (how lines parse to events, how user input gets written to
/// stdin) is injected via the <c>parseLine</c> + <c>serializeInput</c> delegates so this class
/// stays agent-agnostic — Claude uses stream-json on stdin/stdout, Copilot uses one-shot
/// <c>-p</c> + JSONL on stdout (no stdin streaming, so its serializer is null).
/// </summary>
public sealed class SubprocessAgentSession : IAgentSession
{
    private const int SIGTERM = 15;
    private const int ExitGracePeriodSeconds = 5;

    private readonly Process _process;
    private readonly Channel<AgentEvent> _events;
    private readonly Task _stdoutReader;
    private readonly Task _stderrReader;
    private readonly Task _exitWatcher;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _stdinLock = new(1, 1);
    private readonly object _sync = new();
    private readonly Func<string, DateTimeOffset, IEnumerable<AgentEvent>> _parseLine;
    private readonly Func<AgentInput, string>? _serializeInput;
    private bool _finishedEmitted;
    private bool _disposed;

    public string SessionId { get; }
    public string AgentId { get; }

    public IAsyncEnumerable<AgentEvent> Events => _events.Reader.ReadAllAsync();

    public SubprocessAgentSession(
        string sessionId,
        string agentId,
        Process startedProcess,
        ILogger logger,
        Func<string, DateTimeOffset, IEnumerable<AgentEvent>> parseLine,
        Func<AgentInput, string>? serializeInput = null)
    {
        SessionId = sessionId;
        AgentId = agentId;
        _process = startedProcess;
        _logger = logger;
        _parseLine = parseLine;
        _serializeInput = serializeInput;
        _events = Channel.CreateUnbounded<AgentEvent>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false
        });

        _stdoutReader = Task.Run(PumpStdoutAsync);
        _stderrReader = Task.Run(PumpStderrAsync);
        _exitWatcher  = Task.Run(WatchExitAsync);
    }

    // ── Public API ────────────────────────────────────────────────────────

    public async Task SendAsync(AgentInput input, CancellationToken ct = default)
    {
        if (_serializeInput is null)
        {
            // No stdin streaming for this agent — the prompt was already baked in at spawn time.
            // The orchestrator's first SendAsync call after StartSessionAsync becomes a no-op.
            _logger.LogDebug("session {SessionId} ignoring SendAsync — agent doesn't support stdin streaming", SessionId);
            return;
        }
        var json = _serializeInput(input);
        await _stdinLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _process.StandardInput.WriteLineAsync(json.AsMemory(), ct).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _stdinLock.Release();
        }
    }

    public Task PauseAsync(CancellationToken ct = default)
        => throw new NotSupportedException(
            "Pause (SIGSTOP) is scheduled for Phase 2. Use Cancel for now.");

    public Task ResumeAsync(CancellationToken ct = default)
        => throw new NotSupportedException(
            "Resume (SIGCONT) is scheduled for Phase 2.");

    public async Task CancelAsync(CancellationToken ct = default)
    {
        if (_process.HasExited) return;

        try
        {
            // Close stdin first — many CLIs exit gracefully on EOF.
            try { _process.StandardInput.Close(); } catch { }

            if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
            {
                NativeSignal.Kill(_process.Id, SIGTERM);
            }
            else
            {
                // Windows has no SIGTERM; fall through to Kill.
                _process.Kill();
            }

            using var grace = CancellationTokenSource.CreateLinkedTokenSource(ct);
            grace.CancelAfter(TimeSpan.FromSeconds(ExitGracePeriodSeconds));
            try
            {
                await _process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Grace expired → SIGKILL via Process.Kill (entire tree in case of subprocs).
                try { _process.Kill(entireProcessTree: true); } catch { }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error cancelling {AgentId} session {SessionId}", AgentId, SessionId);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (!_process.HasExited)
            {
                await CancelAsync().ConfigureAwait(false);
            }
        }
        catch { /* best-effort */ }

        await Task.WhenAny(
            Task.WhenAll(_stdoutReader, _stderrReader, _exitWatcher),
            Task.Delay(TimeSpan.FromSeconds(2))
        ).ConfigureAwait(false);

        try { _process.Dispose(); } catch { }
        _stdinLock.Dispose();
    }

    // ── Internal pumps ────────────────────────────────────────────────────

    private async Task PumpStdoutAsync()
    {
        try
        {
            string? line;
            while ((line = await _process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                if (line.Length == 0) continue;
                foreach (var ev in _parseLine(line, DateTimeOffset.UtcNow))
                {
                    if (ev is SessionFinishedEvent sf)
                    {
                        EmitFinishedOnce(sf.Success, sf.Error);
                    }
                    else
                    {
                        await _events.Writer.WriteAsync(ev).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "stdout pump exited for session {SessionId}", SessionId);
        }
    }

    private async Task PumpStderrAsync()
    {
        try
        {
            string? line;
            while ((line = await _process.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                if (line.Length == 0) continue;
                // stderr is informational only; emit as LogChunk("stderr", …) for debug surface.
                await _events.Writer.WriteAsync(
                    new LogChunkEvent(DateTimeOffset.UtcNow, "stderr", line + "\n")).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "stderr pump exited for session {SessionId}", SessionId);
        }
    }

    private async Task WatchExitAsync()
    {
        try
        {
            await _process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch { /* best-effort */ }

        // Drain any buffered output after process exit.
        try { await Task.WhenAll(_stdoutReader, _stderrReader).ConfigureAwait(false); } catch { }

        EmitFinishedOnce(success: _process.ExitCode == 0,
                        error: _process.ExitCode == 0 ? null : $"{AgentId} exited with code {_process.ExitCode}");
        _events.Writer.TryComplete();
    }

    private void EmitFinishedOnce(bool success, string? error)
    {
        lock (_sync)
        {
            if (_finishedEmitted) return;
            _finishedEmitted = true;
        }
        _events.Writer.TryWrite(new SessionFinishedEvent(DateTimeOffset.UtcNow, success, error));
    }

}

/// <summary>libc <c>kill(2)</c> for sending POSIX signals. macOS + Linux only.</summary>
internal static class NativeSignal
{
    [DllImport("libc", SetLastError = true, EntryPoint = "kill")]
    private static extern int kill_impl(int pid, int sig);

    public static int Kill(int pid, int sig) => kill_impl(pid, sig);
}
