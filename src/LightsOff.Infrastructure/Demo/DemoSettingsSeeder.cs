using LightsOff.Core.Abstractions;
using LightsOff.Core.Domain;
using LightsOff.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LightsOff.Infrastructure.Demo;

/// <summary>
/// On boot in demo mode, seed Jira settings, fake secrets, the active board, and a single demo
/// repository in the registry — so the user can skip Settings/Boards setup and go straight to
/// the ticket list. Idempotent: re-running on an existing demo state is a no-op.
/// </summary>
internal sealed class DemoSettingsSeeder : IHostedService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<DemoSettingsSeeder> _logger;

    public DemoSettingsSeeder(IServiceScopeFactory scopes, ILogger<DemoSettingsSeeder> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var settings = scope.ServiceProvider.GetRequiredService<IAppSettingsStore>();
            var secrets = scope.ServiceProvider.GetRequiredService<ISecretStore>();
            var repos = scope.ServiceProvider.GetRequiredService<IRepositoryRegistry>();

            await SeedSettingAsync(db, AppSettingKeys.JiraBaseUrl, "https://demo.atlassian.net", cancellationToken).ConfigureAwait(false);
            await SeedSettingAsync(db, AppSettingKeys.JiraEmail, "demo@lights-off.local", cancellationToken).ConfigureAwait(false);
            await SeedSettingAsync(db, AppSettingKeys.JiraApiTokenSecretKey, SecretKeys.JiraApiToken, cancellationToken).ConfigureAwait(false);
            await SeedSettingAsync(db, AppSettingKeys.ActiveBoardId, DemoFixtures.BoardId.ToString(), cancellationToken).ConfigureAwait(false);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            // Default agent → demo. Without this, CodeAgentProvider.GetAgent() falls back to looking
            // up the "local" agent, which we don't register in demo mode.
            if (string.IsNullOrEmpty(await settings.GetAsync(AppSettingsKeys.DefaultAgent, cancellationToken).ConfigureAwait(false)))
                await settings.SetAsync(AppSettingsKeys.DefaultAgent, DemoCodeAgent.AgentIdConst, cancellationToken).ConfigureAwait(false);

            // Placeholder secrets so Settings page shows green and the GitHub Test button (if anyone
            // clicks it during the demo) succeeds.
            if (string.IsNullOrEmpty(await secrets.GetAsync(SecretKeys.JiraApiToken, cancellationToken).ConfigureAwait(false)))
                await secrets.SetAsync(SecretKeys.JiraApiToken, "demo-jira-token", cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(await secrets.GetAsync(SecretKeys.GitHubPat, cancellationToken).ConfigureAwait(false)))
                await secrets.SetAsync(SecretKeys.GitHubPat, "demo-github-pat", cancellationToken).ConfigureAwait(false);

            // Register the demo repo so the ticket detail page can offer it as a target.
            if (await repos.GetAsync(DemoFixtures.DemoRepo, cancellationToken).ConfigureAwait(false) is null)
            {
                await repos.AddAsync(new Repository
                {
                    Name = DemoFixtures.DemoRepo,
                    CloneUrl = $"https://github.com/{DemoFixtures.DemoOwner}/{DemoFixtures.DemoRepo}.git",
                    DefaultBranch = DemoFixtures.DemoDefaultBranch,
                }, cancellationToken).ConfigureAwait(false);
            }

            _logger.LogInformation("Demo mode: seeded Jira settings, active board {Board}, and repo '{Repo}'.",
                DemoFixtures.BoardId, DemoFixtures.DemoRepo);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Demo seeder failed; demo flow may be incomplete.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static async Task SeedSettingAsync(AppDbContext db, string key, string value, CancellationToken ct)
    {
        var existing = await db.AppSettings.FindAsync(new object?[] { key }, ct).ConfigureAwait(false);
        if (existing is null)
            db.AppSettings.Add(new AppSetting { Key = key, Value = value });
        else if (string.IsNullOrEmpty(existing.Value))
            existing.Value = value;
    }
}
