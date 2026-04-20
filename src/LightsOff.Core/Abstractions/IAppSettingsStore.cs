namespace LightsOff.Core.Abstractions;

/// <summary>
/// Generic key-value access to persisted app settings (non-secret).
/// Use <see cref="ISecretStore"/> for credentials.
/// </summary>
public interface IAppSettingsStore
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);
    Task SetAsync(string key, string? value, CancellationToken ct = default);
}

public static class AppSettingsKeys
{
    /// <summary>
    /// Comma-separated list of GitHub organization logins the user picked in Settings.
    /// When set, Browse calls <c>/orgs/{org}/repos</c> for each (token-scoped, includes private + public).
    /// When empty, Browse falls back to <c>/search/repositories?q=is:private</c> (token-scoped private only, no config required).
    /// </summary>
    public const string GitHubOrgs = "github.orgs";

    /// <summary>
    /// ID of the default code agent. <c>"local"</c> = file-system + rules (always available).
    /// <c>"claude"</c>, <c>"qwen"</c>, etc. = AI agents requiring an API key.
    /// </summary>
    public const string DefaultAgent = "agent.default";
}
