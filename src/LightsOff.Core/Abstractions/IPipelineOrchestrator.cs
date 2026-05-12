using LightsOff.Core.Domain;

namespace LightsOff.Core.Abstractions;

/// <summary>
/// Translates a <see cref="PipelineStep"/> into the right sequence of agent calls + DB writes.
/// Called by <c>StepRunnerService</c> from the singleton's fire-and-forget task — the
/// orchestrator does not own concurrency itself and is safe to resolve per-step from a new scope.
/// </summary>
public interface IPipelineOrchestrator
{
    /// <summary>Run the given step for the given ticket. Returns the persisted <see cref="StepRun"/>.</summary>
    Task<StepRun> RunStepAsync(
        string ticketKey,
        PipelineStep step,
        StepRunContext ctx,
        CancellationToken ct = default);
}

/// <summary>
/// Per-run state handed to the orchestrator. <see cref="EmitEvent"/> persists the event to
/// <c>StepEventLog</c> and fans it out to any live UI subscribers via the runner's event bus.
/// <see cref="ParentRunId"/> chains a run to its predecessor (follow-up fix, replan loopback).
/// </summary>
public sealed record StepRunContext(
    string StepRunId,
    string? ParentRunId,
    string AgentId,
    Func<AgentEvent, Task> EmitEvent);
