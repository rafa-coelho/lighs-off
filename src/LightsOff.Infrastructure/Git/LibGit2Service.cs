using LightsOff.Core.Abstractions;
using LibGit2Sharp;
using Microsoft.Extensions.Logging;
using Repository = LightsOff.Core.Domain.Repository;
using GitRepo = LibGit2Sharp.Repository;

namespace LightsOff.Infrastructure.Git;

public class LibGit2Service : IGitService
{
    private readonly ISecretStore _secrets;
    private readonly ILogger<LibGit2Service> _logger;

    public LibGit2Service(ISecretStore secrets, ILogger<LibGit2Service> logger)
    {
        _secrets = secrets;
        _logger = logger;
    }

    public async Task EnsureClonedAsync(Repository repo, string destination, CancellationToken ct = default)
    {
        if (IsGitRepo(destination))
        {
            _logger.LogDebug("Repo already cloned at {Destination}", destination);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var credentials = await GetCredentialsAsync(ct).ConfigureAwait(false);

        await Task.Run(() =>
        {
            var options = new CloneOptions();
            if (credentials is not null)
            {
                options.FetchOptions.CredentialsProvider = (_, _, _) => credentials;
            }
            GitRepo.Clone(repo.CloneUrl, destination, options);
        }, ct).ConfigureAwait(false);

        _logger.LogInformation("Cloned {Repo} to {Destination}", repo.Name, destination);
    }

    public async Task FetchAndPullDefaultAsync(Repository repo, string destination, CancellationToken ct = default)
    {
        var credentials = await GetCredentialsAsync(ct).ConfigureAwait(false);

        await Task.Run(() =>
        {
            using var git = new GitRepo(destination);
            var remote = git.Network.Remotes["origin"];
            var fetchOptions = new FetchOptions();
            if (credentials is not null)
            {
                fetchOptions.CredentialsProvider = (_, _, _) => credentials;
            }
            var refSpecs = remote.FetchRefSpecs.Select(r => r.Specification).ToList();
            Commands.Fetch(git, remote.Name, refSpecs, fetchOptions, logMessage: null);

            var defaultBranch = git.Branches[repo.DefaultBranch]
                               ?? git.Branches[$"origin/{repo.DefaultBranch}"]
                               ?? throw new InvalidOperationException(
                                   $"Default branch '{repo.DefaultBranch}' not found in {repo.Name}.");

            Branch local = defaultBranch.IsRemote
                ? git.CreateBranch(repo.DefaultBranch, defaultBranch.Tip)
                : defaultBranch;

            if (defaultBranch.IsRemote)
            {
                git.Branches.Update(local,
                    b => b.TrackedBranch = defaultBranch.CanonicalName);
            }

            Commands.Checkout(git, local);

            var trackingBranch = local.TrackedBranch ?? git.Branches[$"origin/{repo.DefaultBranch}"];
            if (trackingBranch is not null)
            {
                git.Reset(ResetMode.Hard, trackingBranch.Tip);
            }
        }, ct).ConfigureAwait(false);
    }

    public Task CreateAndCheckoutBranchAsync(string repoPath, string branchName, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            using var git = new GitRepo(repoPath);
            var existing = git.Branches[branchName];
            var branch = existing ?? git.CreateBranch(branchName);
            Commands.Checkout(git, branch);
        }, ct);
    }

    public Task<bool> HasUncommittedChangesAsync(string repoPath, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            using var git = new GitRepo(repoPath);
            var status = git.RetrieveStatus(new LibGit2Sharp.StatusOptions());
            return status.IsDirty;
        }, ct);
    }

    public Task<string> GetDiffAsync(string repoPath, string baseBranch, string headBranch, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            using var git = new GitRepo(repoPath);
            var baseCommit = git.Branches[baseBranch]?.Tip
                          ?? git.Branches[$"origin/{baseBranch}"]?.Tip;
            var headCommit = git.Branches[headBranch]?.Tip ?? git.Head.Tip;
            if (baseCommit is null) return string.Empty;
            var diff = git.Diff.Compare<LibGit2Sharp.Patch>(baseCommit.Tree, headCommit.Tree);
            return diff.Content;
        }, ct);
    }

    public Task<int> CountNewUpstreamCommitsAsync(string repoPath, string defaultBranch, string excludeAuthor, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            using var git = new GitRepo(repoPath);
            var remote = git.Branches[$"origin/{defaultBranch}"];
            var local = git.Branches[defaultBranch];
            if (remote is null || local is null) return 0;

            var filter = new LibGit2Sharp.CommitFilter
            {
                IncludeReachableFrom = remote,
                ExcludeReachableFrom = local
            };
            return git.Commits.QueryBy(filter)
                .Count(c => !c.Author.Name.Equals(excludeAuthor, StringComparison.OrdinalIgnoreCase));
        }, ct);
    }

    public async Task PushBranchAsync(string repoPath, string branchName, CancellationToken ct = default)
    {
        var credentials = await GetCredentialsAsync(ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "GitHub PAT is required to push. Add one in Settings (or configure another credential).");

        await Task.Run(() =>
        {
            using var git = new GitRepo(repoPath);
            var branch = git.Branches[branchName]
                ?? throw new InvalidOperationException($"Branch '{branchName}' not found in {repoPath}.");

            var remote = git.Network.Remotes["origin"]
                ?? throw new InvalidOperationException("Remote 'origin' not configured.");

            git.Branches.Update(branch, b =>
            {
                b.Remote = remote.Name;
                b.UpstreamBranch = branch.CanonicalName;
            });

            var options = new PushOptions
            {
                CredentialsProvider = (_, _, _) => credentials
            };
            git.Network.Push(branch, options);
        }, ct).ConfigureAwait(false);

        _logger.LogInformation("Pushed {Branch} from {RepoPath}", branchName, repoPath);
    }

    private static bool IsGitRepo(string path)
    {
        if (!Directory.Exists(path))
        {
            return false;
        }
        try
        {
            return GitRepo.IsValid(path);
        }
        catch
        {
            return false;
        }
    }

    private async Task<UsernamePasswordCredentials?> GetCredentialsAsync(CancellationToken ct)
    {
        var pat = await _secrets.GetAsync(SecretKeys.GitHubPat, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(pat))
        {
            return null;
        }
        return new UsernamePasswordCredentials { Username = pat, Password = string.Empty };
    }
}
