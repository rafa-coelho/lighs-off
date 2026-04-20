namespace LightsOff.Core.Domain;

public enum WorkspaceStatus
{
    Created,
    Running,
    Stopped,
    Destroyed
}

public class Workspace
{
    public string TicketKey { get; set; } = string.Empty;
    public string TicketSummary { get; set; } = string.Empty;
    public string RootPath { get; set; } = string.Empty;
    public string? ContainerId { get; set; }
    public string? ContainerName { get; set; }
    public WorkspaceStatus Status { get; set; } = WorkspaceStatus.Created;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<TicketRepositoryLink> Links { get; set; } = new();

    /// <summary>Serialized WorkflowState JSON — persists pipeline progress across page reloads.</summary>
    public string? WorkflowStateJson { get; set; }
}
