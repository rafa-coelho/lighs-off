using System.Collections.Concurrent;
using System.Text.Json;
using LightsOff.Core.Abstractions;
using LightsOff.Core.Domain;
using LightsOff.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LightsOff.App.Services;

/// <summary>
/// Singleton runner that owns concurrency for AI-driven pipeline steps. Mirrors the proven
/// <see cref="TestRunnerService"/> pattern: one <see cref="Task.Run"/> per active step, a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> of active runs, and a <see cref="RunUpdated"/>
/// event that UI components re-subscribe to after Blazor circuit reconnects.
///
/// Persists every <see cref="AgentEvent"/> to <c>StepEventLog</c> as it arrives so replay on
/// refresh or app restart is lossless.
///
/// One active step per ticket; N tickets in parallel (uncapped) — matches the agreed concurrency
/// model. Starting a second step for an already-busy ticket is a no-op.
/// </summary>
public sealed class StepRunnerService : IStepSessionSink
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<StepRunnerService> _logger;
    private readonly ConcurrentDictionary<string, ActiveStepRun> _runs = new();

    public event Action<string>? RunUpdated; // fires with ticketKey

    public StepRunnerService(IServiceScopeFactory scopeFactory, ILogger<StepRunnerService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public ActiveStepRun? GetActiveRun(string ticketKey) =>
        _runs.TryGetValue(ticketKey, out var run) ? run : null;

    public bool IsRunning(string ticketKey) =>
        _runs.TryGetValue(ticketKey, out var run) && run.IsRunning;

    /// <summary>
    /// Kick off a new step run for <paramref name="ticketKey"/>. If a run is already active for
    /// the ticket, returns null. Otherwise returns the <see cref="StepRun"/> id so the caller
    /// can navigate or subscribe.
    /// </summary>
    public string? StartStep(string ticketKey, PipelineStep step, string agentId)
    {
        if (IsRunning(ticketKey)) return null;

        var run = new ActiveStepRun
        {
            TicketKey = ticketKey,
            Step = step,
            AgentId = agentId,
            StepRunId = Guid.NewGuid().ToString("N"),
            IsRunning = true,
            Cts = new CancellationTokenSource(),
            Events = new List<AgentEvent>()
        };
        _runs[ticketKey] = run;

        _ = Task.Run(async () =>
        {
            try
            {
                await RunCoreAsync(run).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "StepRunner crashed for ticket {TicketKey} step {Step}", ticketKey, step);
                run.Error = ex.Message;
                // Push the error into the event stream so the UI shows what went wrong instead of
                // a silent "No events yet." Both events get persisted to StepEventLog.
                await EmitErrorEventsAsync(run, ex).ConfigureAwait(false);
                await MarkFinishedAsync(run, StepRunStatus.Failed, ex.Message).ConfigureAwait(false);
            }
            finally
            {
                run.IsRunning = false;
                RunUpdated?.Invoke(ticketKey);
            }
        });

        return run.StepRunId;
    }

    /// <summary>
    /// Run Implement → Validate → Submit as a chained autonomous sequence. Each phase creates
    /// its own <see cref="StepRun"/> row and the active-run slot for the ticket is reassigned
    /// at each transition. Stops the chain on the first failure.
    ///
    /// Returns the StepRunId of the FIRST phase (Implement), or null if a run is already
    /// active for this ticket.
    /// </summary>
    public string? StartAutonomousChain(string ticketKey, string agentId)
    {
        if (IsRunning(ticketKey)) return null;

        // Seed a chain-wide CTS so cancel propagates across all phases.
        var chainCts = new CancellationTokenSource();
        var firstStepRunId = Guid.NewGuid().ToString("N");

        var firstRun = new ActiveStepRun
        {
            TicketKey = ticketKey,
            Step = PipelineStep.Implement,
            AgentId = agentId,
            StepRunId = firstStepRunId,
            IsRunning = true,
            Cts = chainCts,
            Events = new List<AgentEvent>()
        };
        _runs[ticketKey] = firstRun;

        _ = Task.Run(async () =>
        {
            string? prevRunId = null;
            try
            {
                // Phase 1: Implement (uses the run we already inserted).
                var implOk = await RunChainedPhaseAsync(firstRun, parentRunId: prevRunId).ConfigureAwait(false);
                prevRunId = firstRun.StepRunId;
                if (!implOk || chainCts.IsCancellationRequested) return;

                // Phase 2: Validate.
                var validateRun = StartChainedPhase(ticketKey, PipelineStep.Validate, agentId, chainCts);
                var validateOk = await RunChainedPhaseAsync(validateRun, parentRunId: prevRunId).ConfigureAwait(false);
                prevRunId = validateRun.StepRunId;
                if (!validateOk || chainCts.IsCancellationRequested) return;

                // Phase 3: Submit (commit + push + PR).
                var submitRun = StartChainedPhase(ticketKey, PipelineStep.Submit, agentId, chainCts);
                await RunChainedPhaseAsync(submitRun, parentRunId: prevRunId).ConfigureAwait(false);
            }
            finally
            {
                if (_runs.TryGetValue(ticketKey, out var current))
                    current.IsRunning = false;
                RunUpdated?.Invoke(ticketKey);
            }
        });

        return firstStepRunId;
    }

    private ActiveStepRun StartChainedPhase(string ticketKey, PipelineStep step, string agentId, CancellationTokenSource chainCts)
    {
        var run = new ActiveStepRun
        {
            TicketKey = ticketKey,
            Step = step,
            AgentId = agentId,
            StepRunId = Guid.NewGuid().ToString("N"),
            IsRunning = true,
            Cts = chainCts,
            Events = new List<AgentEvent>()
        };
        _runs[ticketKey] = run;
        RunUpdated?.Invoke(ticketKey);
        return run;
    }

    private async Task<bool> RunChainedPhaseAsync(ActiveStepRun run, string? parentRunId)
    {
        try
        {
            return await RunCoreAsync(run, parentRunId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Chained phase {Step} crashed for ticket {TicketKey}", run.Step, run.TicketKey);
            run.Error = ex.Message;
            await EmitErrorEventsAsync(run, ex).ConfigureAwait(false);
            await MarkFinishedAsync(run, StepRunStatus.Failed, ex.Message).ConfigureAwait(false);
            return false;
        }
    }

    /// <summary>Request cancellation of the active run for this ticket (if any).</summary>
    public Task CancelAsync(string ticketKey)
    {
        if (!_runs.TryGetValue(ticketKey, out var run)) return Task.CompletedTask;
        run.CancellationReason ??= "User cancelled";
        try { run.Cts.Cancel(); } catch { }
        // The orchestrator is listening on Cts.Token and will tear down the underlying session.
        return Task.CompletedTask;
    }

    /// <summary>Deliver a chat or answer input to the active session, if one exists.</summary>
    public Task SendInputAsync(string ticketKey, AgentInput input)
    {
        if (!_runs.TryGetValue(ticketKey, out var run) || run.Session is null)
            return Task.CompletedTask;
        return run.Session.SendAsync(input);
    }

    /// <summary>
    /// Called by the orchestrator once it has an open <see cref="IAgentSession"/> so the runner
    /// can route user-sent chat/answer inputs into it. No-op if the ticket has no active run.
    /// </summary>
    public void AttachSession(string ticketKey, IAgentSession session)
    {
        if (_runs.TryGetValue(ticketKey, out var run))
            run.Session = session;
    }

    // ── Core run loop ─────────────────────────────────────────────────────

    private Task RunCoreAsync(ActiveStepRun run) => RunCoreAsync(run, parentRunId: null);

    /// <summary>
    /// Returns true if the step ended in <see cref="StepRunStatus.Succeeded"/>; false otherwise
    /// (Failed, Cancelled, etc.). Used by the autonomous chain to decide whether to proceed.
    /// </summary>
    private async Task<bool> RunCoreAsync(ActiveStepRun run, string? parentRunId)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var orchestrator = scope.ServiceProvider.GetRequiredService<IPipelineOrchestrator>();

        // 1. Persist a StepRun row so UI can see it immediately.
        var stepRun = new StepRun
        {
            Id = run.StepRunId,
            TicketKey = run.TicketKey,
            Step = run.Step,
            Status = StepRunStatus.Running,
            AgentId = run.AgentId,
            StartedAt = DateTimeOffset.UtcNow,
            ParentRunId = parentRunId
        };
        db.StepRuns.Add(stepRun);
        await db.SaveChangesAsync().ConfigureAwait(false);
        RunUpdated?.Invoke(run.TicketKey);

        // 2. Hand off to the orchestrator with an EmitEvent callback that persists + fans out.
        var seq = 0;
        async Task EmitAsync(AgentEvent ev)
        {
            var mySeq = Interlocked.Increment(ref seq);
            var entry = new StepEventLogEntry
            {
                StepRunId = run.StepRunId,
                Seq = mySeq,
                At = ev.At,
                Kind = ev.GetType().Name,
                PayloadJson = JsonSerializer.Serialize((object)ev)
            };

            // Each event writes on its own connection — cheaper than keeping an open transaction
            // across an unbounded stream, and the (StepRunId, Seq) unique index keeps ordering.
            await using var innerScope = _scopeFactory.CreateAsyncScope();
            var innerDb = innerScope.ServiceProvider.GetRequiredService<AppDbContext>();
            innerDb.StepEventLog.Add(entry);
            await innerDb.SaveChangesAsync().ConfigureAwait(false);

            run.Events.Add(ev);
            run.LatestAt = ev.At;
            RunUpdated?.Invoke(run.TicketKey);
        }

        var ctx = new StepRunContext(
            StepRunId: run.StepRunId,
            ParentRunId: parentRunId,
            AgentId: run.AgentId,
            EmitEvent: EmitAsync);

        try
        {
            var completed = await orchestrator.RunStepAsync(
                run.TicketKey, run.Step, ctx, run.Cts.Token).ConfigureAwait(false);

            await MarkFinishedAsync(run, completed.Status, completed.CancellationReason).ConfigureAwait(false);
            return completed.Status == StepRunStatus.Succeeded;
        }
        catch (OperationCanceledException)
        {
            await MarkFinishedAsync(run, StepRunStatus.Cancelled, run.CancellationReason ?? "Cancelled").ConfigureAwait(false);
            return false;
        }
    }

    private async Task EmitErrorEventsAsync(ActiveStepRun run, Exception ex)
    {
        // Synthesize two events directly: a stderr LogChunk with the message + a SessionFinished
        // marker so the StepLogViewer renders the same red footer it does for real claude errors.
        // Persist both to StepEventLog so replay-after-restart shows the failure too.
        var seqStart = run.Events.Count;
        var logEv = new LogChunkEvent(DateTimeOffset.UtcNow, "stderr", ex.Message + "\n");
        var finEv = new SessionFinishedEvent(DateTimeOffset.UtcNow, Success: false, Error: ex.Message);

        await PersistEventAsync(run.StepRunId, seqStart + 1, logEv).ConfigureAwait(false);
        await PersistEventAsync(run.StepRunId, seqStart + 2, finEv).ConfigureAwait(false);

        run.Events.Add(logEv);
        run.Events.Add(finEv);
        run.LatestAt = finEv.At;
        RunUpdated?.Invoke(run.TicketKey);
    }

    private async Task PersistEventAsync(string stepRunId, int seq, AgentEvent ev)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.StepEventLog.Add(new StepEventLogEntry
            {
                StepRunId = stepRunId,
                Seq = seq,
                At = ev.At,
                Kind = ev.GetType().Name,
                PayloadJson = System.Text.Json.JsonSerializer.Serialize((object)ev)
            });
            await db.SaveChangesAsync().ConfigureAwait(false);
        }
        catch (Exception persistEx)
        {
            _logger.LogWarning(persistEx, "Failed to persist error event for step run {StepRunId}", stepRunId);
        }
    }

    private async Task MarkFinishedAsync(ActiveStepRun run, StepRunStatus status, string? reason)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.StepRuns.FirstOrDefaultAsync(r => r.Id == run.StepRunId).ConfigureAwait(false);
        if (row is null) return;
        row.Status = status;
        row.FinishedAt = DateTimeOffset.UtcNow;
        row.CancellationReason = reason;
        await db.SaveChangesAsync().ConfigureAwait(false);

        run.FinalStatus = status;
        RunUpdated?.Invoke(run.TicketKey);

        // Keep the ActiveStepRun in-memory briefly so UI can show the terminal state,
        // then drop it to free the event list. Give the UI 60s to pick it up.
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
            _runs.TryRemove(run.TicketKey, out _);
        });
    }
}

public sealed class ActiveStepRun
{
    public string TicketKey { get; set; } = string.Empty;
    public string StepRunId { get; set; } = string.Empty;
    public PipelineStep Step { get; set; }
    public string AgentId { get; set; } = string.Empty;
    public bool IsRunning { get; set; }
    public StepRunStatus FinalStatus { get; set; } = StepRunStatus.Running;
    public string? Error { get; set; }
    public string? CancellationReason { get; set; }
    public DateTimeOffset? LatestAt { get; set; }
    public List<AgentEvent> Events { get; set; } = new();
    public CancellationTokenSource Cts { get; set; } = new();
    internal IAgentSession? Session { get; set; }
}
