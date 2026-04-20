namespace LightsOff.Core.Domain;

/// <summary>
/// Shape of an optional <c>.lightsoff.yaml</c> file at the root of a repo.
/// Drives container image, post-start commands, and host/container port mappings.
/// </summary>
public class RepoConfig
{
    /// <summary>Override the default container image for this repo.</summary>
    public string? Image { get; set; }

    /// <summary>Primary test command (e.g. <c>npm test</c>). Used to seed the test config on first run.</summary>
    public string? TestCommand { get; set; }

    /// <summary>Shell commands run inside the container after it starts (e.g. <c>npm install</c>).</summary>
    public List<string> PostCreateCommands { get; set; } = new();

    /// <summary>Port mappings like <c>"3000:3000"</c> or <c>"5432:5432"</c>.</summary>
    public List<string> Ports { get; set; } = new();

    /// <summary>Environment variables to inject.</summary>
    public Dictionary<string, string> Env { get; set; } = new(StringComparer.Ordinal);
}
