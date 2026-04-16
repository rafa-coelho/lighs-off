using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using LightsOff.Core.Abstractions;
using LightsOff.Core.Domain;

namespace LightsOff.Cli;

public class ApiException : Exception
{
    public int StatusCode { get; }
    public ApiException(int statusCode, string message) : base(message) => StatusCode = statusCode;
}

public class ApiClient
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;

    public ApiClient(HttpClient http)
    {
        _http = http;
    }

    public static ApiClient Create(string baseUrl)
    {
        return new ApiClient(new HttpClient
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(60)
        });
    }

    public Task<IReadOnlyList<Board>> GetBoardsAsync(CancellationToken ct = default)
        => GetAsync<IReadOnlyList<Board>>("api/boards", ct);

    public Task<IReadOnlyList<Ticket>> GetTicketsAsync(int boardId, string? jql, CancellationToken ct = default)
    {
        var path = $"api/tickets?boardId={boardId}";
        if (!string.IsNullOrWhiteSpace(jql))
        {
            path += $"&jql={Uri.EscapeDataString(jql)}";
        }
        return GetAsync<IReadOnlyList<Ticket>>(path, ct);
    }

    public Task<IReadOnlyList<Repository>> GetReposAsync(CancellationToken ct = default)
        => GetAsync<IReadOnlyList<Repository>>("api/repos", ct);

    public async Task<Repository> AddRepoAsync(Repository repo, CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync("api/repos", repo, ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<Repository>(_json, ct))!;
    }

    public async Task<Repository> UpdateRepoAsync(Repository repo, CancellationToken ct = default)
    {
        using var response = await _http.PutAsJsonAsync($"api/repos/{Uri.EscapeDataString(repo.Name)}", repo, ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<Repository>(_json, ct))!;
    }

    public async Task RemoveRepoAsync(string name, CancellationToken ct = default)
    {
        using var response = await _http.DeleteAsync($"api/repos/{Uri.EscapeDataString(name)}", ct);
        await EnsureSuccessAsync(response, ct);
    }

    public Task<IReadOnlyList<Workspace>> GetWorkspacesAsync(CancellationToken ct = default)
        => GetAsync<IReadOnlyList<Workspace>>("api/workspaces", ct);

    public async Task<Workspace> CreateWorkspaceAsync(CreateWorkspaceRequest request, CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync("api/workspaces", request, ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<Workspace>(_json, ct))!;
    }

    public async Task DestroyWorkspaceAsync(string ticketKey, CancellationToken ct = default)
    {
        using var response = await _http.DeleteAsync($"api/workspaces/{Uri.EscapeDataString(ticketKey)}", ct);
        await EnsureSuccessAsync(response, ct);
    }

    public Task<Workspace> GetWorkspaceAsync(string ticketKey, CancellationToken ct = default)
        => GetAsync<Workspace>($"api/workspaces/{Uri.EscapeDataString(ticketKey)}", ct);

    public async Task StartWorkspaceAsync(string ticketKey, CancellationToken ct = default)
    {
        using var response = await _http.PostAsync($"api/workspaces/{Uri.EscapeDataString(ticketKey)}/start", content: null, ct);
        await EnsureSuccessAsync(response, ct);
    }

    public async Task StopWorkspaceAsync(string ticketKey, CancellationToken ct = default)
    {
        using var response = await _http.PostAsync($"api/workspaces/{Uri.EscapeDataString(ticketKey)}/stop", content: null, ct);
        await EnsureSuccessAsync(response, ct);
    }

    public async Task<IReadOnlyList<PullRequestResult>> SubmitWorkspaceAsync(string ticketKey, CancellationToken ct = default)
    {
        using var response = await _http.PostAsync($"api/workspaces/{Uri.EscapeDataString(ticketKey)}/submit", content: null, ct);
        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<IReadOnlyList<PullRequestResult>>(_json, ct))!;
    }

    public async Task SaveJiraConnectionAsync(string baseUrl, string email, string apiToken, CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync("api/jira/connection",
            new { baseUrl, email, apiToken }, ct);
        await EnsureSuccessAsync(response, ct);
    }

    public async Task SaveGithubPatAsync(string? token, CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync("api/github/pat", new { token }, ct);
        await EnsureSuccessAsync(response, ct);
    }

    private async Task<T> GetAsync<T>(string path, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(path, ct);
            await EnsureSuccessAsync(response, ct);
            return (await response.Content.ReadFromJsonAsync<T>(_json, ct))!;
        }
        catch (HttpRequestException ex) when (ex.StatusCode is null)
        {
            throw new ApiException(0, $"Cannot reach {_http.BaseAddress}. Is the App running? ({ex.Message})");
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;

        var body = await response.Content.ReadAsStringAsync(ct);
        string? message = null;
        if (response.Content.Headers.ContentType?.MediaType is "application/problem+json" or "application/json")
        {
            try
            {
                var problem = JsonSerializer.Deserialize<ProblemShape>(body, _json);
                message = problem?.Detail ?? problem?.Title ?? problem?.Error;
            }
            catch
            {
                // fall through to raw body
            }
        }
        message ??= string.IsNullOrWhiteSpace(body)
            ? response.ReasonPhrase ?? "Unknown error"
            : (body.Length > 300 ? body[..300] + "…" : body);
        throw new ApiException((int)response.StatusCode, message);
    }

    private sealed class ProblemShape
    {
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("detail")] public string? Detail { get; set; }
        [JsonPropertyName("error")] public string? Error { get; set; }
    }
}
