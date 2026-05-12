using LibGit2Sharp;
using LightsOff.Core.Abstractions;
using LightsOff.Infrastructure.Git;
using Microsoft.Extensions.Logging.Abstractions;

namespace LightsOff.Tests;

/// <summary>
/// End-to-end test for <see cref="LibGit2Service.CommitAllAsync"/> against a real temp repo.
/// Verifies staging, commit creation, no-op on clean tree, and uncommitted-files listing.
/// </summary>
public class LibGit2ServiceCommitTests : IDisposable
{
    private readonly string _repoPath;
    private readonly LibGit2Service _git;

    public LibGit2ServiceCommitTests()
    {
        _repoPath = Path.Combine(Path.GetTempPath(), "lightsoff-git-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_repoPath);
        Repository.Init(_repoPath);

        // Initial commit — needed so HEAD has something to compare against.
        File.WriteAllText(Path.Combine(_repoPath, "README.md"), "# test\n");
        using (var git = new Repository(_repoPath))
        {
            Commands.Stage(git, "*");
            var sig = new Signature("seed", "seed@test", DateTimeOffset.UtcNow);
            git.Commit("initial", sig, sig);
        }

        var secrets = new InMemorySecretStore();
        _git = new LibGit2Service(secrets, NullLogger<LibGit2Service>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_repoPath, recursive: true); } catch { }
    }

    [Fact]
    public async Task CommitAllAsync_stages_and_commits_changes()
    {
        File.WriteAllText(Path.Combine(_repoPath, "new.txt"), "hello\n");
        File.AppendAllText(Path.Combine(_repoPath, "README.md"), "more\n");

        var sha = await _git.CommitAllAsync(_repoPath, "test commit", "alice", "alice@test");

        Assert.NotNull(sha);
        Assert.Equal(40, sha!.Length);

        using var repo = new Repository(_repoPath);
        Assert.Equal("test commit\n", repo.Head.Tip.Message);
        Assert.Equal("alice", repo.Head.Tip.Author.Name);
    }

    [Fact]
    public async Task CommitAllAsync_returns_null_when_tree_is_clean()
    {
        var sha = await _git.CommitAllAsync(_repoPath, "should not commit", "alice", "alice@test");
        Assert.Null(sha);
    }

    [Fact]
    public async Task GetUncommittedFilesAsync_lists_modified_added_and_deleted()
    {
        File.WriteAllText(Path.Combine(_repoPath, "added.txt"), "new\n");
        File.AppendAllText(Path.Combine(_repoPath, "README.md"), "edited\n");

        var paths = await _git.GetUncommittedFilesAsync(_repoPath);

        Assert.Contains("added.txt", paths);
        Assert.Contains("README.md", paths);
    }

    [Fact]
    public async Task GetUncommittedFilesAsync_returns_empty_when_clean()
    {
        var paths = await _git.GetUncommittedFilesAsync(_repoPath);
        Assert.Empty(paths);
    }

    private sealed class InMemorySecretStore : ISecretStore
    {
        public Task<string?> GetAsync(string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task SetAsync(string key, string value, CancellationToken ct = default) => Task.CompletedTask;
        public Task RemoveAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
    }
}
