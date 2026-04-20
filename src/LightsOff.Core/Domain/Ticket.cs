namespace LightsOff.Core.Domain;

public class Ticket
{
    // Basic (Phase 0 — always populated)
    public string Key { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Assignee { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;

    // Rich (Phase A3 — populated from expanded Jira fetch)
    public string? Description { get; set; }
    public string? AcceptanceCriteria { get; set; }
    public double? StoryPoints { get; set; }
    public string? EpicKey { get; set; }
    public string? EpicName { get; set; }
    public List<string> Labels { get; set; } = new();
    public List<string> Components { get; set; } = new();
    public List<LinkedIssue> LinkedIssues { get; set; } = new();
    public List<TicketComment> Comments { get; set; } = new();
    public List<SubTask> SubTasks { get; set; } = new();
}

public record LinkedIssue(string Key, string Summary, string Status, string LinkType);
public record TicketComment(string Author, string Body, DateTimeOffset CreatedAt);
public record SubTask(string Key, string Summary, string Status);
