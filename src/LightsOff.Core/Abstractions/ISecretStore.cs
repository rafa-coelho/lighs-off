namespace LightsOff.Core.Abstractions;

public static class SecretKeys
{
    public const string JiraApiToken = "jira.apiToken";
    public const string GitHubPat = "github.pat";
    public const string ClaudeApiKey = "claude.apiKey";
}

public interface ISecretStore
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);
    Task SetAsync(string key, string value, CancellationToken ct = default);
    Task RemoveAsync(string key, CancellationToken ct = default);
}
