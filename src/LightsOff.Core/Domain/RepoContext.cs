namespace LightsOff.Core.Domain;

public class RepoContext
{
    public string RepoName { get; set; } = string.Empty;
    public DateTimeOffset AnalyzedAt { get; set; } = DateTimeOffset.UtcNow;
    public string AgentUsed { get; set; } = "local";

    // File-system analysis (local agent fills these)
    public string TechStack { get; set; } = string.Empty;
    public string DirectoryTree { get; set; } = string.Empty;
    public List<string> EntryPoints { get; set; } = new();
    public List<string> ConfigFiles { get; set; } = new();
    public List<ApiEndpoint> ApiSurface { get; set; } = new();
    public List<DataEntity> DataModel { get; set; } = new();
    public RepoTestSetup? TestInfo { get; set; }
    public List<RepoDependency> TopDependencies { get; set; } = new();
    public List<GitCommitSummary> RecentHistory { get; set; } = new();
    public string? ReadmeSummary { get; set; }

    // Deep analysis (AI agent fills these — null for local agent)
    public string? ArchitectureOverview { get; set; }
    public List<RepoFeature>? Features { get; set; }
    public string? DataFlowDescription { get; set; }
    public List<string>? BusinessDomains { get; set; }
    public List<string>? CodeQualityNotes { get; set; }
}

public record ApiEndpoint(string Method, string Route, string? File, int? Line);
public record DataEntity(string Name, string? File);
public record RepoTestSetup(string Framework, string? ConfigFile, List<string> TestDirectories);
public record RepoDependency(string Name, string Version);
public record GitCommitSummary(string Hash, string Message, string Author, DateTimeOffset Date);
public record RepoFeature(string Name, string? Description, List<string>? RelatedFiles);
