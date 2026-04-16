namespace LightsOff.Core.Configuration;

public class LightsOffOptions
{
    public const string SectionName = "LightsOff";

    public string WorkspacesRoot { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".lightsoff",
        "workspaces");

    public string ReposRoot { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".lightsoff",
        "repos");

    public string StateRoot { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".lightsoff");

    public string DefaultContainerImage { get; set; } = "mcr.microsoft.com/devcontainers/base:ubuntu";

    public string BranchNameTemplate { get; set; } = "feature/{key}-{slug}";

    public int? ActiveBoardId { get; set; }
}
