namespace LightsOff.Core.Domain;

public record TestResult(
    DateTimeOffset RanAt,
    int ExitCode,
    bool Passed,
    string Output,
    double? CoveragePercent);

public class TestSuiteResult
{
    public DateTimeOffset RanAt { get; set; } = DateTimeOffset.UtcNow;
    public List<TestRunResult> Results { get; set; } = new();
    public bool AllPassed => Results.Count > 0 && Results.All(r => r.AllPassed);
    public int PassedCount => Results.Count(r => r.AllPassed);
    public int FailedCount => Results.Count(r => !r.AllPassed);
}

public class TestRunResult
{
    public string ConfigName { get; set; } = string.Empty;
    public string RepositoryName { get; set; } = string.Empty;
    public List<CommandStep> Steps { get; set; } = new();
    public bool AllPassed => Steps.Count > 0 && Steps.All(s => s.Passed == true);
    public bool HasFailure => Steps.Any(s => s.Passed == false);
    public bool IsRunning => Steps.Any(s => s.IsRunning);
}

public class CommandStep
{
    public string Command { get; set; } = string.Empty;
    public bool IsRunning { get; set; }
    public int? ExitCode { get; set; }
    public bool? Passed { get; set; }
    public string Output { get; set; } = string.Empty;
    public TimeSpan? Duration { get; set; }
}
