namespace LightsOff.Core.Domain;

public sealed record GitHubRepo(
    string FullName,
    string Name,
    string OwnerLogin,
    string CloneUrl,
    string SshUrl,
    string DefaultBranch,
    bool Private,
    string? Description,
    DateTimeOffset? PushedAt);
