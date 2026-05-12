namespace LightsOff.Core.Domain;

/// <summary>
/// One execution of a single pipeline step. Multiple runs per ticket per step are normal:
/// re-runs, follow-up fixes, and Submit→Request-changes loopbacks all create new rows. A run
/// is identified by <see cref="Id"/> (guid string) and grouped with its parent via
/// <see cref="ParentRunId"/>.
/// </summary>
public class StepRun
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string TicketKey { get; set; } = string.Empty;
    public PipelineStep Step { get; set; }
    public StepRunStatus Status { get; set; } = StepRunStatus.Running;
    public string AgentId { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
    public string? CancellationReason { get; set; }
    public string? ParentRunId { get; set; }
    public string? SummaryJson { get; set; }
}

public enum StepRunStatus
{
    Running,
    Paused,
    Succeeded,
    Failed,
    Cancelled,
    Interrupted
}

/// <summary>
/// Append-only per-run stream of agent events. <see cref="Seq"/> is a monotonic counter scoped
/// to the parent <see cref="StepRunId"/>; replay loads entries in (StepRunId, Seq) order.
/// <see cref="Kind"/> is the event record type name (e.g. "LogChunkEvent"); <see cref="PayloadJson"/>
/// is the serialized event.
/// </summary>
public class StepEventLogEntry
{
    public long Id { get; set; }
    public string StepRunId { get; set; } = string.Empty;
    public int Seq { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    public string Kind { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
}

/// <summary>
/// Cache of a repo's latest full analysis keyed by its HEAD sha. On the next Analyze step, if
/// HEAD is unchanged we reuse <see cref="ContextJson"/> instead of re-scanning; if it differs
/// we re-analyze and overwrite.
/// </summary>
public class RepoAnalysisCacheEntry
{
    public string RepoName { get; set; } = string.Empty;
    public string HeadSha { get; set; } = string.Empty;
    public DateTimeOffset AnalyzedAt { get; set; } = DateTimeOffset.UtcNow;
    public string AgentId { get; set; } = string.Empty;
    public string ContextJson { get; set; } = string.Empty;
}
