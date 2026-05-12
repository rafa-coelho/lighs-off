namespace LightsOff.Core.Domain;

/// <summary>
/// AI assessment of whether a diff actually satisfies a ticket's acceptance criteria. Used as a
/// warning surface in the Validate step — concerns are shown but never block Submit, since
/// AC text is often vague enough that AI verdicts have a meaningful false-positive rate.
/// </summary>
public sealed class AcReviewReport
{
    public string TicketKey { get; set; } = string.Empty;
    public string AgentUsed { get; set; } = "local";
    public DateTimeOffset ReviewedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Best-effort verdict. False does NOT block Submit — it's surfaced as a warning.</summary>
    public bool SatisfiesAc { get; set; } = true;

    /// <summary>Specific concerns the reviewer raised. Empty when SatisfiesAc is true.</summary>
    public List<string> Concerns { get; set; } = new();

    /// <summary>One-paragraph summary of how the diff addresses the AC.</summary>
    public string Summary { get; set; } = string.Empty;
}
