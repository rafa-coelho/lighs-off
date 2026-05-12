using LightsOff.Core.Abstractions;
using LightsOff.Core.Configuration;
using LightsOff.Infrastructure.Configuration;
using LightsOff.Infrastructure.Data;
using LightsOff.Infrastructure.Agents;
using LightsOff.Infrastructure.Demo;
using LightsOff.Infrastructure.Docker;
using LightsOff.Infrastructure.Git;
using LightsOff.Infrastructure.GitHub;
using LightsOff.Infrastructure.Jira;
using LightsOff.Infrastructure.Registry;
using LightsOff.Infrastructure.Pipeline;
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

        var isDemo = IsDemoMode(configuration);
        if (isDemo)
        {
            // Route state/repos/workspaces under ~/.lightsoff-demo/ so the real state.db stays
            // untouched. The user can wipe the demo dir between presentations without risk.
            services.PostConfigure<LightsOffOptions>(opts =>
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var demoRoot = Path.Combine(home, ".lightsoff-demo");
                opts.StateRoot = demoRoot;
                opts.WorkspacesRoot = Path.Combine(demoRoot, "workspaces");
                opts.ReposRoot = Path.Combine(demoRoot, "repos");
                opts.ActiveBoardId = DemoFixtures.BoardId;
            });
        }

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
        services.AddScoped<IRepositoryRegistry, RepositoryRegistry>();
        services.AddSingleton<Workspaces.RepoConfigLoader>();

        if (isDemo)
        {
            services.AddScoped<IJiraClient, DemoJiraClient>();
            services.AddScoped<IGitHubClient, DemoGitHubClient>();
            services.AddScoped<IGitService, DemoGitService>();
            services.AddSingleton<IDockerService, DemoDockerService>();
            services.AddScoped<ICodeAgent, DemoCodeAgent>();
            services.AddScoped<ICodeAgentProvider, CodeAgentProvider>();
            services.AddScoped<IWorkspaceManager, DemoWorkspaceManager>();
            services.AddScoped<IPipelineOrchestrator, DemoPipelineOrchestrator>();
            services.AddHostedService<DemoSettingsSeeder>();
        }
        else
        {
            services.AddScoped<IJiraClient, JiraClient>();
            services.AddScoped<IGitHubClient, GitHubClient>();
            services.AddScoped<IGitService, LibGit2Service>();
            services.AddSingleton<IDockerService, DockerService>();
            services.AddScoped<LocalCodeAgent>();
            services.AddScoped<ICodeAgent>(sp => sp.GetRequiredService<LocalCodeAgent>());
            services.AddScoped<ICodeAgent, ClaudeCodeAgent>();
            services.AddScoped<ICodeAgent, ClaudeCliAgent>();
            services.AddScoped<ICodeAgent, CopilotCliAgent>();
            services.AddScoped<ICodeAgentProvider, CodeAgentProvider>();
            services.AddScoped<IWorkspaceManager, WorkspaceManager>();
            services.AddScoped<IPipelineOrchestrator, PipelineOrchestrator>();
        }

        return services;
    }

    /// <summary>
    /// Demo mode is on when either <c>LightsOff:DemoMode</c> is true in configuration or the
    /// <c>LIGHTSOFF_DEMO</c> env var is set to a truthy value. The env var is the convenient
    /// switch for a presentation laptop where editing appsettings.json is overkill.
    /// </summary>
    private static bool IsDemoMode(IConfiguration configuration)
    {
        if (configuration.GetValue<bool>("LightsOff:DemoMode")) return true;
        var env = Environment.GetEnvironmentVariable("LIGHTSOFF_DEMO");
        return !string.IsNullOrWhiteSpace(env)
               && env is not "0" and not "false" and not "False" and not "FALSE";
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

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "StepRuns" (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "TicketKey" TEXT NOT NULL,
                "Step" TEXT NOT NULL,
                "Status" TEXT NOT NULL,
                "AgentId" TEXT NOT NULL,
                "StartedAt" INTEGER NOT NULL,
                "FinishedAt" INTEGER NULL,
                "CancellationReason" TEXT NULL,
                "ParentRunId" TEXT NULL,
                "SummaryJson" TEXT NULL
            )
            """, ct).ConfigureAwait(false);
        await db.Database.ExecuteSqlRawAsync(
            """CREATE INDEX IF NOT EXISTS "IX_StepRuns_TicketKey_Step" ON "StepRuns" ("TicketKey", "Step")""",
            ct).ConfigureAwait(false);
        await db.Database.ExecuteSqlRawAsync(
            """CREATE INDEX IF NOT EXISTS "IX_StepRuns_ParentRunId" ON "StepRuns" ("ParentRunId")""",
            ct).ConfigureAwait(false);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "StepEventLog" (
                "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                "StepRunId" TEXT NOT NULL,
                "Seq" INTEGER NOT NULL,
                "At" INTEGER NOT NULL,
                "Kind" TEXT NOT NULL,
                "PayloadJson" TEXT NOT NULL
            )
            """, ct).ConfigureAwait(false);
        await db.Database.ExecuteSqlRawAsync(
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_StepEventLog_StepRunId_Seq" ON "StepEventLog" ("StepRunId", "Seq")""",
            ct).ConfigureAwait(false);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "RepoAnalysisCache" (
                "RepoName" TEXT NOT NULL,
                "HeadSha" TEXT NOT NULL,
                "AnalyzedAt" INTEGER NOT NULL,
                "AgentId" TEXT NOT NULL,
                "ContextJson" TEXT NOT NULL,
                PRIMARY KEY ("RepoName", "HeadSha")
            )
            """, ct).ConfigureAwait(false);
    }
}
