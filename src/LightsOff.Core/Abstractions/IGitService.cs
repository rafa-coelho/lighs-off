using LightsOff.Core.Domain;

namespace LightsOff.Core.Abstractions;

public interface IGitService
{
    Task EnsureClonedAsync(Repository repo, string destination, CancellationToken ct = default);
    Task FetchAndPullDefaultAsync(Repository repo, string destination, CancellationToken ct = default);
    Task CreateAndCheckoutBranchAsync(string repoPath, string branchName, CancellationToken ct = default);
    Task PushBranchAsync(string repoPath, string branchName, CancellationToken ct = default);
    Task<string> GetDiffAsync(string repoPath, string baseBranch, string headBranch, CancellationToken ct = default);
    Task<bool> HasUncommittedChangesAsync(string repoPath, CancellationToken ct = default);

    /// <summary>
    /// Returns the number of new commits on origin/{defaultBranch} not authored by <paramref name="excludeAuthor"/>.
    /// Used for change detection: "did someone else push while I was away?"
    /// </summary>
    Task<int> CountNewUpstreamCommitsAsync(string repoPath, string defaultBranch, string excludeAuthor, CancellationToken ct = default);

    /// <summary>
    /// Returns the full SHA of HEAD. Used to key the repo-analysis cache so we skip re-analysis
    /// when HEAD hasn't moved since the last run.
    /// </summary>
    Task<string> GetHeadShaAsync(string repoPath, CancellationToken ct = default);

    /// <summary>
    /// Stage all changes in the working tree and create a commit with the supplied message
    /// + author identity. No-ops cleanly if there's nothing to commit (returns false). Returns
    /// the commit SHA on success.
    /// </summary>
    Task<string?> CommitAllAsync(
        string repoPath,
        string message,
        string authorName,
        string authorEmail,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the list of paths with uncommitted changes (modified, added, deleted, untracked).
    /// Used to surface the diff context to the commit-message prompt and to decide whether
    /// commit should run at all.
    /// </summary>
    Task<IReadOnlyList<string>> GetUncommittedFilesAsync(string repoPath, CancellationToken ct = default);
}
