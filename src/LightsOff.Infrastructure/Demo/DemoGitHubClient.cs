using LightsOff.Core.Abstractions;
using LightsOff.Core.Domain;

namespace LightsOff.Infrastructure.Demo;

/// <summary>
/// Demo GitHub: returns a single canned repo and produces realistic-looking PR URLs.
/// </summary>
internal sealed class DemoGitHubClient : IGitHubClient
{
    private static readonly TimeSpan Latency = TimeSpan.FromMilliseconds(200);
    private int _prCounter = 41;

    public async Task<IReadOnlyList<GitHubRepo>> ListAccessibleReposAsync(CancellationToken ct = default)
    {
        await Task.Delay(Latency, ct).ConfigureAwait(false);
        return new[]
        {
            new GitHubRepo(
                FullName: $"{DemoFixtures.DemoOwner}/{DemoFixtures.DemoRepo}",
                Name: DemoFixtures.DemoRepo,
                OwnerLogin: DemoFixtures.DemoOwner,
                CloneUrl: $"https://github.com/{DemoFixtures.DemoOwner}/{DemoFixtures.DemoRepo}.git",
                SshUrl: $"git@github.com:{DemoFixtures.DemoOwner}/{DemoFixtures.DemoRepo}.git",
                DefaultBranch: DemoFixtures.DemoDefaultBranch,
                Private: true,
                Description: "Checkout API for the demo storefront.",
                PushedAt: DateTimeOffset.UtcNow.AddDays(-1)),
        };
    }

    public async Task<IReadOnlyList<string>> ListUserOrgsAsync(CancellationToken ct = default)
    {
        await Task.Delay(Latency, ct).ConfigureAwait(false);
        return new[] { DemoFixtures.DemoOwner };
    }

    public async Task<bool> TestPatAsync(string pat, CancellationToken ct = default)
    {
        await Task.Delay(Latency, ct).ConfigureAwait(false);
        return true;
    }

    public async Task<string> CreatePullRequestAsync(
        string owner,
        string repo,
        string headBranch,
        string baseBranch,
        string title,
        string body,
        CancellationToken ct = default)
    {
        await Task.Delay(Latency, ct).ConfigureAwait(false);
        var number = Interlocked.Increment(ref _prCounter);
        return $"https://github.com/{owner}/{repo}/pull/{number}";
    }

    public async Task<(string Url, string State)?> FindPullRequestForBranchAsync(
        string owner,
        string repo,
        string headBranch,
        CancellationToken ct = default)
    {
        await Task.Delay(Latency, ct).ConfigureAwait(false);
        return null;
    }
}
