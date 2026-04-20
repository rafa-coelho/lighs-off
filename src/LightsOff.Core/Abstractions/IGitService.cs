using LightsOff.Core.Domain;

namespace LightsOff.Core.Abstractions;

public interface IGitService
{
    Task EnsureClonedAsync(Repository repo, string destination, CancellationToken ct = default);
    Task FetchAndPullDefaultAsync(Repository repo, string destination, CancellationToken ct = default);
    Task CreateAndCheckoutBranchAsync(string repoPath, string branchName, CancellationToken ct = default);
    Task PushBranchAsync(string repoPath, string branchName, CancellationToken ct = default);
    Task<string> GetDiffAsync(string repoPath, string baseBranch, string headBranch, CancellationToken ct = default);

    /// <summary>
    /// Returns the number of new commits on origin/{defaultBranch} not authored by <paramref name="excludeAuthor"/>.
    /// Used for change detection: "did someone else push while I was away?"
    /// </summary>
    Task<int> CountNewUpstreamCommitsAsync(string repoPath, string defaultBranch, string excludeAuthor, CancellationToken ct = default);
}
