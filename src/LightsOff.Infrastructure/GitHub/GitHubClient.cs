using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using LightsOff.Core.Abstractions;
using LightsOff.Core.Domain;
using Microsoft.Extensions.Logging;

namespace LightsOff.Infrastructure.GitHub;

public class GitHubClient : IGitHubClient
{
    public const string HttpClientName = "github";
    private const string BaseAddress = "https://api.github.com/";
    private const string UserAgent = "lights-off/0.1";

    // Default path when no org is pinned: Search honors fine-grained PAT scope exactly.
    // Returns only private repos the token can see. 30 req/min rate limit, plenty for on-demand browsing.
    private const string PrivateReposSearchUrl =
        "search/repositories?q=is:private&sort=updated&order=desc&per_page=100";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ISecretStore _secrets;
    private readonly IAppSettingsStore _settings;
    private readonly ILogger<GitHubClient> _logger;

    public GitHubClient(
        IHttpClientFactory httpClientFactory,
        ISecretStore secrets,
        IAppSettingsStore settings,
        ILogger<GitHubClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _secrets = secrets;
        _settings = settings;
        _logger = logger;
    }

    public async Task<IReadOnlyList<GitHubRepo>> ListAccessibleReposAsync(CancellationToken ct = default)
    {
        var pat = await _secrets.GetAsync(SecretKeys.GitHubPat, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("GitHub PAT is not configured. Open Settings to add one.");

        using var client = CreateClient(pat);

        var orgs = ParseOrgs(await _settings.GetAsync(AppSettingsKeys.GitHubOrgs, ct).ConfigureAwait(false));

        try
        {
            if (orgs.Count == 0)
            {
                // No orgs picked → use Search, which honors fine-grained PAT scope strictly for private repos.
                var searchDtos = await FetchReposAsync(client, PrivateReposSearchUrl, isSearch: true, ct).ConfigureAwait(false);
                _logger.LogDebug("GitHub search is:private → {Count} repos (token-scoped)", searchDtos.Count);
                return searchDtos
                    .OrderByDescending(r => r.PushedAt ?? DateTimeOffset.MinValue)
                    .Select(Map)
                    .ToList();
            }

            // Orgs picked → fetch each in parallel, but tolerate per-org failures so one bad entry
            // doesn't blow up the whole list.
            var tasks = orgs.Select(async org =>
            {
                try
                {
                    return await FetchReposAsync(client,
                        $"orgs/{Uri.EscapeDataString(org)}/repos?per_page=100&sort=pushed&direction=desc",
                        isSearch: false, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to fetch repos for org {Org}; skipping", org);
                    return new List<RepoDto>();
                }
            }).ToList();
            var pages = await Task.WhenAll(tasks).ConfigureAwait(false);

            var combined = pages
                .SelectMany(p => p)
                .GroupBy(r => r.FullName, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderByDescending(r => r.PushedAt ?? DateTimeOffset.MinValue)
                .Select(Map)
                .ToList();

            _logger.LogDebug("GitHub /orgs/{{org}}/repos × {OrgCount} → {Count} unique repos", orgs.Count, combined.Count);
            return combined;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new InvalidOperationException("GitHub authentication failed. Check the PAT in Settings.", ex);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException(
                "GitHub refused the request (403). The PAT may lack the right permissions, be SSO-unauthorized, or be rate-limited.", ex);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException(
                "One of the pinned organizations was not found (or your token can't see it). Re-pick the orgs in Settings.", ex);
        }
    }

    private static List<string> ParseOrgs(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new List<string>();
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<string>> ListUserOrgsAsync(CancellationToken ct = default)
    {
        var pat = await _secrets.GetAsync(SecretKeys.GitHubPat, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("GitHub PAT is not configured. Open Settings to add one.");

        using var client = CreateClient(pat);

        // Preferred: `/user/orgs` — returns every org the user is a member of on GitHub.
        // Requires Organization Members (read) permission on the PAT; many fine-grained PATs lack it.
        try
        {
            var orgs = await client.GetFromJsonAsync<List<OrgDto>>("user/orgs?per_page=100", ct).ConfigureAwait(false);
            var fromUserOrgs = (orgs ?? new List<OrgDto>())
                .Where(o => !string.IsNullOrWhiteSpace(o.Login))
                .Select(o => o.Login!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (fromUserOrgs.Count > 0)
            {
                return fromUserOrgs.OrderBy(l => l, StringComparer.OrdinalIgnoreCase).ToList();
            }
            // Empty response → fall through to the repo-derived fallback.
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new InvalidOperationException("GitHub authentication failed. Check the PAT in Settings.", ex);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
        {
            _logger.LogDebug(ex, "GET /user/orgs forbidden; deriving orgs from accessible repos instead");
        }

        // Fallback: enumerate repos the user has any org-affiliation to, then pull distinct
        // Organization-type owners. Works on fine-grained PATs without Organization Members permission.
        try
        {
            var dtos = await FetchReposAsync(client,
                "user/repos?affiliation=organization_member&per_page=100&sort=updated",
                isSearch: false, ct).ConfigureAwait(false);
            return dtos
                .Where(r => string.Equals(r.Owner?.Type, "Organization", StringComparison.OrdinalIgnoreCase))
                .Select(r => r.Owner!.Login)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => l!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(l => l, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new InvalidOperationException("GitHub authentication failed. Check the PAT in Settings.", ex);
        }
    }

    public async Task<bool> TestPatAsync(string pat, CancellationToken ct = default)
    {
        using var client = CreateClient(pat);
        try
        {
            using var response = await client.GetAsync("user", ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GitHub PAT test failed");
            return false;
        }
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
        var pat = await _secrets.GetAsync(SecretKeys.GitHubPat, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("GitHub PAT is not configured. Open Settings to add one.");

        using var client = CreateClient(pat);
        var url = $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/pulls";
        var payload = new
        {
            title,
            body,
            head = headBranch,
            @base = baseBranch
        };

        using var response = await client.PostAsJsonAsync(url, payload, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var detail = TryExtractMessage(errorBody) ?? errorBody;
            throw new InvalidOperationException(
                $"GitHub refused PR creation for {owner}/{repo} ({(int)response.StatusCode}): {detail}");
        }

        var pr = await response.Content.ReadFromJsonAsync<PullRequestDto>(cancellationToken: ct).ConfigureAwait(false);
        return pr?.HtmlUrl
            ?? throw new InvalidOperationException("GitHub returned no html_url for the new PR.");
    }

    private static string? TryExtractMessage(string body)
    {
        try
        {
            var doc = System.Text.Json.JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("message", out var msg))
            {
                var baseMsg = msg.GetString();
                if (doc.RootElement.TryGetProperty("errors", out var errs) && errs.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    var details = errs.EnumerateArray()
                        .Select(e => e.TryGetProperty("message", out var m) ? m.GetString() : null)
                        .Where(s => !string.IsNullOrEmpty(s));
                    var joined = string.Join("; ", details);
                    if (!string.IsNullOrEmpty(joined))
                    {
                        return $"{baseMsg} — {joined}";
                    }
                }
                return baseMsg;
            }
        }
        catch
        {
            // fall through
        }
        return null;
    }

    public async Task<(string Url, string State)?> FindPullRequestForBranchAsync(
        string owner,
        string repo,
        string headBranch,
        CancellationToken ct = default)
    {
        var pat = await _secrets.GetAsync(SecretKeys.GitHubPat, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("GitHub PAT is not configured.");

        using var client = CreateClient(pat);

        // Try open first (most common). If none, try closed (the user might have closed/merged it).
        foreach (var state in new[] { "open", "all" })
        {
            var url = $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/pulls" +
                      $"?head={Uri.EscapeDataString(owner)}:{Uri.EscapeDataString(headBranch)}&state={state}&per_page=1";
            using var response = await client.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) continue;
            var prs = await response.Content.ReadFromJsonAsync<List<PullRequestDto>>(cancellationToken: ct).ConfigureAwait(false);
            var pr = prs?.FirstOrDefault();
            if (pr?.HtmlUrl is null) continue;

            // Normalize state: GitHub returns "open"/"closed"; we surface "merged" separately when possible.
            var displayState = pr.State ?? state;
            if (string.Equals(displayState, "closed", StringComparison.OrdinalIgnoreCase) && pr.MergedAt is not null)
                displayState = "merged";
            return (pr.HtmlUrl, displayState.ToLowerInvariant());
        }
        return null;
    }

    private sealed class PullRequestDto
    {
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
        [JsonPropertyName("number")] public int Number { get; set; }
        [JsonPropertyName("state")] public string? State { get; set; }
        [JsonPropertyName("merged_at")] public DateTimeOffset? MergedAt { get; set; }
    }

    private async Task<List<RepoDto>> FetchReposAsync(HttpClient client, string firstUrl, bool isSearch, CancellationToken ct)
    {
        var all = new List<RepoDto>();
        var url = firstUrl;
        while (!string.IsNullOrEmpty(url))
        {
            using var response = await client.GetAsync(url, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            if (isSearch)
            {
                var page = await response.Content.ReadFromJsonAsync<SearchResponse>(cancellationToken: ct).ConfigureAwait(false);
                if (page?.Items is { Count: > 0 })
                {
                    all.AddRange(page.Items);
                }
            }
            else
            {
                var page = await response.Content.ReadFromJsonAsync<List<RepoDto>>(cancellationToken: ct).ConfigureAwait(false);
                if (page is { Count: > 0 })
                {
                    all.AddRange(page);
                }
            }

            url = ParseNextLink(response.Headers);
            if (all.Count >= 500) break;
        }
        return all;
    }

    private static string? ParseNextLink(HttpResponseHeaders headers)
    {
        if (!headers.TryGetValues("Link", out var values)) return null;
        foreach (var header in values)
        {
            foreach (var part in header.Split(','))
            {
                var segments = part.Split(';');
                if (segments.Length < 2) continue;
                var url = segments[0].Trim().TrimStart('<').TrimEnd('>');
                var rel = segments[1].Trim();
                if (rel.Contains("rel=\"next\"", StringComparison.Ordinal))
                {
                    if (Uri.TryCreate(url, UriKind.Absolute, out var abs))
                    {
                        return abs.PathAndQuery.TrimStart('/');
                    }
                    return url;
                }
            }
        }
        return null;
    }

    private HttpClient CreateClient(string pat)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        client.BaseAddress = new Uri(BaseAddress);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", pat);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    private static GitHubRepo Map(RepoDto dto) => new(
        dto.FullName ?? string.Empty,
        dto.Name ?? string.Empty,
        dto.Owner?.Login ?? string.Empty,
        dto.CloneUrl ?? string.Empty,
        dto.SshUrl ?? string.Empty,
        dto.DefaultBranch ?? "main",
        dto.Private,
        dto.Description,
        dto.PushedAt);

    private sealed class SearchResponse
    {
        [JsonPropertyName("total_count")] public int TotalCount { get; set; }
        [JsonPropertyName("items")] public List<RepoDto>? Items { get; set; }
    }

    private sealed class RepoDto
    {
        [JsonPropertyName("full_name")] public string? FullName { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("owner")] public OwnerDto? Owner { get; set; }
        [JsonPropertyName("clone_url")] public string? CloneUrl { get; set; }
        [JsonPropertyName("ssh_url")] public string? SshUrl { get; set; }
        [JsonPropertyName("default_branch")] public string? DefaultBranch { get; set; }
        [JsonPropertyName("private")] public bool Private { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("pushed_at")] public DateTimeOffset? PushedAt { get; set; }
    }

    private sealed class OwnerDto
    {
        [JsonPropertyName("login")] public string? Login { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
    }

    private sealed class OrgDto
    {
        [JsonPropertyName("login")] public string? Login { get; set; }
    }
}
