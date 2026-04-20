namespace LightsOff.Core.Domain;

public record TestResult(
    DateTimeOffset RanAt,
    int ExitCode,
    bool Passed,
    string Output,
    double? CoveragePercent);
