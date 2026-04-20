namespace LightsOff.Core.Domain;

public class RepoDependencyProfile
{
    public string RepositoryName { get; set; } = string.Empty;
    public DateTimeOffset DetectedAt { get; set; } = DateTimeOffset.UtcNow;
    public string AgentUsed { get; set; } = "local";
    public List<RuntimeRequirement> Runtimes { get; set; } = new();
    public List<string> PackageInstallCommands { get; set; } = new();
    public List<string> CustomCommands { get; set; } = new();
}

public record RuntimeRequirement(
    string Runtime,
    string Version,
    string? SubPath);
