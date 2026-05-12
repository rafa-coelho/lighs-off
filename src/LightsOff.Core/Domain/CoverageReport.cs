namespace LightsOff.Core.Domain;

/// <summary>
/// Result of measuring coverage on code that is new relative to a base branch. The agent produces
/// this; the Validate step gates on <see cref="NewCodeLinePercent"/> vs. a threshold (default 80%).
/// </summary>
public sealed class CoverageReport
{
    public string RepositoryName { get; set; } = string.Empty;
    public string AgentUsed { get; set; } = "local";
    public DateTimeOffset MeasuredAt { get; set; } = DateTimeOffset.UtcNow;

    public double OverallLinePercent { get; set; }
    public double NewCodeLinePercent { get; set; }

    public int NewCodeLinesTotal { get; set; }
    public int NewCodeLinesCovered { get; set; }

    public List<CoverageFile> Files { get; set; } = new();

    /// <summary>
    /// Set when the report couldn't be produced — missing coverage tool, no test output, unreadable
    /// format, etc. Non-null means the Validate step should show an error, not gate on percentages.
    /// </summary>
    public string? Error { get; set; }
}

public sealed record CoverageFile(
    string Path,
    double LinePercent,
    int NewLinesTotal,
    int NewLinesCovered,
    IReadOnlyList<int> UncoveredNewLines);
