namespace LightsOff.Core.Domain;

public class RepoTestConfig
{
    public int Id { get; set; }
    public string RepositoryName { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public string? WorkingDir { get; set; }
    public int Order { get; set; }
}
