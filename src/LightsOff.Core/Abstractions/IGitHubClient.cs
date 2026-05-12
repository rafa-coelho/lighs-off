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

    /// <summary>
    /// Finds an existing pull request for <paramref name="headBranch"/>. Returns its html_url + state
    /// ("open"/"closed"/"merged") or null if none. Used after a 422 "already exists" response so
    /// callers can recover gracefully and report the existing PR instead of failing.
    /// </summary>
    Task<(string Url, string State)?> FindPullRequestForBranchAsync(
        string owner,
        string repo,
        string headBranch,
        CancellationToken ct = default);
}
