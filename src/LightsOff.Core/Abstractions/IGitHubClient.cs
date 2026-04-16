using LightsOff.Core.Domain;

namespace LightsOff.Core.Abstractions;

public interface IGitHubClient
{
    Task<IReadOnlyList<GitHubRepo>> ListAccessibleReposAsync(CancellationToken ct = default);
    Task<IReadOnlyList<string>> ListUserOrgsAsync(CancellationToken ct = default);
    Task<bool> TestPatAsync(string pat, CancellationToken ct = default);
}
