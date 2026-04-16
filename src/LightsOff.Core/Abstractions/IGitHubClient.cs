using LightsOff.Core.Domain;

namespace LightsOff.Core.Abstractions;

public interface IGitHubClient
{
    Task<IReadOnlyList<GitHubRepo>> ListAccessibleReposAsync(CancellationToken ct = default);
    Task<IReadOnlyList<string>> ListUserOrgsAsync(CancellationToken ct = default);
    Task<bool> TestPatAsync(string pat, CancellationToken ct = default);

    /// <summary>Creates a pull request. Returns the html_url of the new PR.</summary>
    Task<string> CreatePullRequestAsync(
        string owner,
        string repo,
        string headBranch,
        string baseBranch,
        string title,
        string body,
        CancellationToken ct = default);
}
