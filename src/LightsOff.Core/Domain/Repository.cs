namespace LightsOff.Core.Domain;

public class Repository
{
    public string Name { get; set; } = string.Empty;
    public string CloneUrl { get; set; } = string.Empty;
    public string DefaultBranch { get; set; } = "main";
    public string? Image { get; set; }
}
