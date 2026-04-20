namespace LightsOff.Core.Domain;

public class TicketAnalysis
{
    public string TicketKey { get; set; } = string.Empty;
    public DateTimeOffset AnalyzedAt { get; set; } = DateTimeOffset.UtcNow;
    public string AgentUsed { get; set; } = "local";

    public List<Gap> Gaps { get; set; } = new();
    public List<TicketTask> Tasks { get; set; } = new();
    public List<RelevantFile> RelevantFiles { get; set; } = new();
    public string? AiSummary { get; set; }

    public bool HasErrors => Gaps.Any(g => g.Severity == GapSeverity.Error);
    public bool HasWarnings => Gaps.Any(g => g.Severity == GapSeverity.Warning);
}

public enum GapSeverity { Error, Warning, Info }
public record Gap(GapSeverity Severity, string Rule, string Detail);
public record TicketTask(int Order, string Description, bool FromTicket);
public record RelevantFile(string Path, string Reason);
