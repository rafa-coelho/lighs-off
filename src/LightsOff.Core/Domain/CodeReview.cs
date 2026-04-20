namespace LightsOff.Core.Domain;

public class CodeReview
{
    public DateTimeOffset ReviewedAt { get; set; } = DateTimeOffset.UtcNow;
    public string AgentUsed { get; set; } = "local";
    public List<SecurityFinding> SecurityFindings { get; set; } = new();
    public List<QualityFinding> QualityFindings { get; set; } = new();
    public string? AiSummary { get; set; }

    public int TotalFindings => SecurityFindings.Count + QualityFindings.Count;
    public bool HasCritical => SecurityFindings.Any(f =>
        f.Severity is "critical" or "high");
}

public record SecurityFinding(string Severity, string Pattern, string File, int? Line, string Detail);
public record QualityFinding(string Category, string File, string Detail);
