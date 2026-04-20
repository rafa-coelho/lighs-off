using LightsOff.Core.Abstractions;
using LightsOff.Core.Configuration;
using LightsOff.Infrastructure.Configuration;
using LightsOff.Infrastructure.Data;
using LightsOff.Infrastructure.Agents;
using LightsOff.Infrastructure.Docker;
using LightsOff.Infrastructure.Git;
using LightsOff.Infrastructure.GitHub;
using LightsOff.Infrastructure.Jira;
using LightsOff.Infrastructure.Registry;
using LightsOff.Infrastructure.Secrets;
using LightsOff.Infrastructure.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LightsOff.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddLightsOffInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<LightsOffOptions>(configuration.GetSection(LightsOffOptions.SectionName));
        services.AddHttpClient(JiraClient.HttpClientName);
        services.AddHttpClient(GitHubClient.HttpClientName);

        services.AddDbContext<AppDbContext>((sp, options) =>
        {
            var settings = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<LightsOffOptions>>().Value;
            Directory.CreateDirectory(settings.StateRoot);
            var dbPath = Path.Combine(settings.StateRoot, "state.db");
            options.UseSqlite($"Data Source={dbPath}");
        });

        services.AddSingleton<ISecretStore, FileSecretStore>();
        services.AddScoped<IAppSettingsStore, AppSettingsStore>();
        services.AddScoped<IJiraConnectionProvider, JiraConnectionProvider>();
        services.AddScoped<IJiraClient, JiraClient>();
        services.AddScoped<IGitHubClient, GitHubClient>();
        services.AddScoped<IGitService, LibGit2Service>();
        services.AddSingleton<IDockerService, DockerService>();
        services.AddScoped<IRepositoryRegistry, RepositoryRegistry>();
        services.AddSingleton<Workspaces.RepoConfigLoader>();
        services.AddScoped<LocalCodeAgent>();
        services.AddScoped<ICodeAgent>(sp => sp.GetRequiredService<LocalCodeAgent>());
        services.AddScoped<ICodeAgent, ClaudeCodeAgent>();
        services.AddScoped<ICodeAgentProvider, CodeAgentProvider>();
        services.AddScoped<IWorkspaceManager, WorkspaceManager>();

        return services;
    }

    public static async Task EnsureDatabaseCreatedAsync(this IServiceProvider serviceProvider, CancellationToken ct = default)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.EnsureCreatedAsync(ct).ConfigureAwait(false);

        // Patch: add tables that were introduced after the initial EnsureCreated.
        // EnsureCreated won't add new tables to an existing DB — this covers schema evolution
        // without requiring full EF Core migrations for a personal tool.
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "RepoTestConfigs" (
                "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                "RepositoryName" TEXT NOT NULL,
                "Name" TEXT NOT NULL,
                "Command" TEXT NOT NULL,
                "WorkingDir" TEXT,
                "Order" INTEGER NOT NULL
            )
            """, ct).ConfigureAwait(false);

        // Add WorkflowStateJson column to Workspaces if missing (schema evolution)
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                """ALTER TABLE "Workspaces" ADD COLUMN "WorkflowStateJson" TEXT""", ct).ConfigureAwait(false);
        }
        catch { /* column already exists */ }

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "RepoDependencyProfiles" (
                "RepositoryName" TEXT NOT NULL PRIMARY KEY,
                "DetectedAt" TEXT NOT NULL,
                "AgentUsed" TEXT NOT NULL,
                "RuntimesJson" TEXT NOT NULL DEFAULT '[]',
                "PackageInstallCommandsJson" TEXT NOT NULL DEFAULT '[]',
                "CustomCommandsJson" TEXT NOT NULL DEFAULT '[]'
            )
            """, ct).ConfigureAwait(false);
    }
}
