using LightsOff.Core.Abstractions;
using LightsOff.Core.Domain;

namespace LightsOff.Infrastructure.Demo;

/// <summary>
/// No-op git service. The demo orchestrator + workspace manager never need real git; this
/// implementation keeps the DI graph satisfied and returns plausible values for any path that
/// does get exercised (e.g. someone clicking "view diff" outside the orchestrated flow).
/// </summary>
internal sealed class DemoGitService : IGitService
{
    public Task EnsureClonedAsync(Repository repo, string destination, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task FetchAndPullDefaultAsync(Repository repo, string destination, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task CreateAndCheckoutBranchAsync(string repoPath, string branchName, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task PushBranchAsync(string repoPath, string branchName, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<string> GetDiffAsync(string repoPath, string baseBranch, string headBranch, CancellationToken ct = default)
        => Task.FromResult(DemoDiff);

    public Task<bool> HasUncommittedChangesAsync(string repoPath, CancellationToken ct = default)
        => Task.FromResult(false);

    public Task<int> CountNewUpstreamCommitsAsync(string repoPath, string defaultBranch, string excludeAuthor, CancellationToken ct = default)
        => Task.FromResult(0);

    public Task<string> GetHeadShaAsync(string repoPath, CancellationToken ct = default)
        => Task.FromResult("a1b2c3d4e5f60718293a4b5c6d7e8f9012345678");

    public Task<string?> CommitAllAsync(string repoPath, string message, string authorName, string authorEmail, CancellationToken ct = default)
        => Task.FromResult<string?>("a1b2c3d4e5f60718293a4b5c6d7e8f9012345678");

    public Task<IReadOnlyList<string>> GetUncommittedFilesAsync(string repoPath, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    private const string DemoDiff =
        """
        diff --git a/src/Api/Middleware/RateLimitMiddleware.cs b/src/Api/Middleware/RateLimitMiddleware.cs
        new file mode 100644
        --- /dev/null
        +++ b/src/Api/Middleware/RateLimitMiddleware.cs
        @@ -0,0 +1,52 @@
        +public sealed class RateLimitMiddleware { /* token bucket */ }
        """;
}
