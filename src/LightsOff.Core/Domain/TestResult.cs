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
    public List<SingleTestResult> Results { get; set; } = new();
    public bool AllPassed => Results.Count > 0 && Results.All(r => r.Passed);
    public int PassedCount => Results.Count(r => r.Passed);
    public int FailedCount => Results.Count(r => !r.Passed);
}

public record SingleTestResult(
    string ConfigName,
    string Command,
    string? WorkingDir,
    string RepositoryName,
    int ExitCode,
    bool Passed,
    string Output);
