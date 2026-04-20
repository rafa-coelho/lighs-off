namespace LightsOff.Core.Domain;

public class ImplementationPlan
{
    public string TicketKey { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string AgentUsed { get; set; } = "local";
    public PlanStatus Status { get; set; } = PlanStatus.Draft;

    public string Approach { get; set; } = string.Empty;
    public List<PlannedTask> Tasks { get; set; } = new();
    public List<FileChange> PlannedChanges { get; set; } = new();
    public List<string> Assumptions { get; set; } = new();
    public List<string> OpenQuestions { get; set; } = new();
    public List<PlanComment> Comments { get; set; } = new();

    public bool IsApproved => Status == PlanStatus.Approved;
    public bool NeedsAction => Status is PlanStatus.Draft or PlanStatus.AwaitingApproval or PlanStatus.ChangesRequested;
}

public enum PlanStatus { Draft, AwaitingApproval, ChangesRequested, Approved, Rejected }
public enum ChangeType { Create, Modify, Delete }
public enum PlanCommentType { Question, ChangeRequest, Approval, Rejection, Note }

public record PlannedTask(int Order, string Description, string? TargetFile, string? Rationale);
public record FileChange(string Path, ChangeType Type, string? Reason);
public record PlanComment(DateTimeOffset At, string Author, PlanCommentType Type, string Content);
