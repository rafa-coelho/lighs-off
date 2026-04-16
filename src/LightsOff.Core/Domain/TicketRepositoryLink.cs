namespace LightsOff.Core.Domain;

public class TicketRepositoryLink
{
    public int Id { get; set; }
    public string TicketKey { get; set; } = string.Empty;
    public string RepositoryName { get; set; } = string.Empty;
    public string BranchName { get; set; } = string.Empty;
}
