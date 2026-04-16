using LightsOff.Core.Domain;

namespace LightsOff.Core.Abstractions;

public interface IGitService
{
    Task EnsureClonedAsync(Repository repo, string destination, CancellationToken ct = default);
    Task FetchAndPullDefaultAsync(Repository repo, string destination, CancellationToken ct = default);
    Task CreateAndCheckoutBranchAsync(string repoPath, string branchName, CancellationToken ct = default);
    Task PushBranchAsync(string repoPath, string branchName, CancellationToken ct = default);
}
