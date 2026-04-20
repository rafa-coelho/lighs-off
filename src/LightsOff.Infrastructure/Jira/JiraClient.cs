using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using LightsOff.Core.Abstractions;
using LightsOff.Core.Domain;
using Microsoft.Extensions.Logging;

namespace LightsOff.Infrastructure.Jira;

public class JiraClient : IJiraClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IJiraConnectionProvider _connections;
    private readonly ISecretStore _secrets;
    private readonly ILogger<JiraClient> _logger;

    public const string HttpClientName = "jira";

    public JiraClient(
        IHttpClientFactory httpClientFactory,
        IJiraConnectionProvider connections,
        ISecretStore secrets,
        ILogger<JiraClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _connections = connections;
        _secrets = secrets;
        _logger = logger;
    }

    public async Task<bool> TestConnectionAsync(JiraConnection connection, string apiToken, CancellationToken ct = default)
    {
        using var client = CreateClient(connection, apiToken);
        try
        {
            using var response = await client.GetAsync("/rest/api/3/myself", ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Jira test connection failed for {BaseUrl}", connection.BaseUrl);
            return false;
        }
    }

    public async Task<IReadOnlyList<Board>> GetBoardsAsync(CancellationToken ct = default)
    {
        using var client = await CreateConfiguredClientAsync(ct).ConfigureAwait(false);
        var page = await client.GetFromJsonAsync<BoardsPage>("/rest/agile/1.0/board?maxResults=50", ct)
            .ConfigureAwait(false);
        if (page?.Values is null)
        {
            return Array.Empty<Board>();
        }

        return page.Values
            .Select(v => new Board(v.Id, v.Name, v.Location?.ProjectKey, v.Type))
            .ToList();
    }

    public async Task<IReadOnlyList<Ticket>> GetBoardIssuesAsync(int boardId, string? jql = null, CancellationToken ct = default)
    {
        using var client = await CreateConfiguredClientAsync(ct).ConfigureAwait(false);
        var path = $"/rest/agile/1.0/board/{boardId}/issue?maxResults=100";
        if (!string.IsNullOrWhiteSpace(jql))
        {
            path += $"&jql={Uri.EscapeDataString(jql)}";
        }

        var page = await client.GetFromJsonAsync<IssuesPage>(path, ct).ConfigureAwait(false);
        if (page?.Issues is null)
        {
            return Array.Empty<Ticket>();
        }

        var baseUrl = client.BaseAddress?.ToString().TrimEnd('/') ?? string.Empty;
        return page.Issues.Select(i => MapIssue(i, baseUrl)).ToList();
    }

    public async Task<Ticket?> GetIssueAsync(string key, CancellationToken ct = default)
    {
        using var client = await CreateConfiguredClientAsync(ct).ConfigureAwait(false);
        try
        {
            var issue = await client.GetFromJsonAsync<IssueDto>($"/rest/api/3/issue/{Uri.EscapeDataString(key)}?expand=renderedFields", ct)
                .ConfigureAwait(false);
            if (issue is null)
            {
                return null;
            }
            var baseUrl = client.BaseAddress?.ToString().TrimEnd('/') ?? string.Empty;
            return MapIssue(issue, baseUrl);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private static Ticket MapIssue(IssueDto issue, string baseUrl) => new()
    {
        Key = issue.Key,
        Summary = issue.Fields?.Summary ?? string.Empty,
        Status = issue.Fields?.Status?.Name ?? "Unknown",
        Assignee = issue.Fields?.Assignee?.DisplayName,
        Type = issue.Fields?.IssueType?.Name ?? "Task",
        Url = $"{baseUrl}/browse/{issue.Key}",
        Description = issue.RenderedFields?.Description,
        StoryPoints = issue.Fields?.StoryPoints,
        Labels = issue.Fields?.Labels ?? new(),
        Components = issue.Fields?.Components?.Select(c => c.Name ?? "").Where(n => n.Length > 0).ToList() ?? new(),
    };

    private async Task<HttpClient> CreateConfiguredClientAsync(CancellationToken ct)
    {
        var connection = await _connections.GetAsync(ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Jira connection is not configured. Open Settings to configure it first.");
        var token = await _secrets.GetAsync(connection.ApiTokenSecretKey, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Jira API token is missing. Reconfigure the Jira connection.");
        return CreateClient(connection, token);
    }

    private HttpClient CreateClient(JiraConnection connection, string apiToken)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        client.BaseAddress = new Uri(connection.BaseUrl.TrimEnd('/') + "/");
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{connection.Email}:{apiToken}"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", basic);
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    private sealed class BoardsPage
    {
        [JsonPropertyName("values")] public List<BoardDto>? Values { get; set; }
    }

    private sealed class BoardDto
    {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("location")] public BoardLocationDto? Location { get; set; }
    }

    private sealed class BoardLocationDto
    {
        [JsonPropertyName("projectKey")] public string? ProjectKey { get; set; }
    }

    private sealed class IssuesPage
    {
        [JsonPropertyName("issues")] public List<IssueDto>? Issues { get; set; }
    }

    private sealed class IssueDto
    {
        [JsonPropertyName("key")] public string Key { get; set; } = string.Empty;
        [JsonPropertyName("fields")] public IssueFieldsDto? Fields { get; set; }
        [JsonPropertyName("renderedFields")] public RenderedFieldsDto? RenderedFields { get; set; }
    }

    private sealed class IssueFieldsDto
    {
        [JsonPropertyName("summary")] public string? Summary { get; set; }
        [JsonPropertyName("status")] public NamedDto? Status { get; set; }
        [JsonPropertyName("assignee")] public AssigneeDto? Assignee { get; set; }
        [JsonPropertyName("issuetype")] public NamedDto? IssueType { get; set; }
        [JsonPropertyName("description")] public object? Description { get; set; }
        [JsonPropertyName("story_points")] public double? StoryPoints { get; set; }
        [JsonPropertyName("labels")] public List<string>? Labels { get; set; }
        [JsonPropertyName("components")] public List<NamedDto>? Components { get; set; }
    }

    private sealed class RenderedFieldsDto
    {
        [JsonPropertyName("description")] public string? Description { get; set; }
    }

    private sealed class NamedDto
    {
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    }

    private sealed class AssigneeDto
    {
        [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
    }
}
