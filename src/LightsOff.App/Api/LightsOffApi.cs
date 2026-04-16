using LightsOff.Core.Abstractions;
using LightsOff.Core.Domain;
using LightsOff.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace LightsOff.App.Api;

public static class LightsOffApi
{
    public static IEndpointRouteBuilder MapLightsOffApi(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api").WithTags("lights-off");

        api.AddEndpointFilter(async (ctx, next) =>
        {
            try
            {
                return await next(ctx);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(
                    title: "Precondition failed",
                    detail: ex.Message,
                    statusCode: StatusCodes.Status428PreconditionRequired);
            }
            catch (KeyNotFoundException ex)
            {
                return Results.Problem(
                    title: "Not found",
                    detail: ex.Message,
                    statusCode: StatusCodes.Status404NotFound);
            }
            catch (ArgumentException ex)
            {
                return Results.Problem(
                    title: "Invalid request",
                    detail: ex.Message,
                    statusCode: StatusCodes.Status400BadRequest);
            }
        });

        MapHealth(api);
        MapJira(api);
        MapRepos(api);
        MapWorkspaces(api);

        return endpoints;
    }

    private static void MapHealth(RouteGroupBuilder api)
    {
        api.MapGet("/health", async (
            IDockerService docker,
            IJiraClient jira,
            IJiraConnectionProvider jiraConn,
            ISecretStore secrets,
            AppDbContext db,
            CancellationToken ct) =>
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));

            var dbTask = CheckAsync(async () =>
                await db.Database.CanConnectAsync(cts.Token)
                    ? new { status = "ok" }
                    : new { status = "unreachable" });

            var dockerTask = CheckAsync(async () =>
                await docker.IsDockerAvailableAsync(cts.Token)
                    ? new { status = "ok" }
                    : new { status = "unreachable" });

            var jiraTask = CheckAsync<object>(async () =>
            {
                var connection = await jiraConn.GetAsync(cts.Token);
                if (connection is null) return new { status = "not-configured" };
                var token = await secrets.GetAsync(connection.ApiTokenSecretKey, cts.Token);
                if (string.IsNullOrEmpty(token)) return new { status = "missing-token" };
                var ok = await jira.TestConnectionAsync(connection, token, cts.Token);
                return ok
                    ? new { status = "ok", baseUrl = connection.BaseUrl }
                    : new { status = "unreachable", baseUrl = connection.BaseUrl };
            });

            var githubTask = CheckAsync<object>(async () =>
            {
                var pat = await secrets.GetAsync(SecretKeys.GitHubPat, cts.Token);
                return string.IsNullOrEmpty(pat)
                    ? new { status = "not-configured" }
                    : new { status = "configured" };
            });

            await Task.WhenAll(dbTask, dockerTask, jiraTask, githubTask);

            var dbResult = await dbTask;
            var dockerResult = await dockerTask;
            var jiraResult = await jiraTask;
            var githubResult = await githubTask;

            var allOk = IsOk(dbResult) && IsOk(dockerResult);
            return Results.Ok(new
            {
                status = allOk ? "healthy" : "degraded",
                version = typeof(LightsOffApi).Assembly.GetName().Version?.ToString() ?? "0.1.0",
                checks = new
                {
                    database = dbResult,
                    docker = dockerResult,
                    jira = jiraResult,
                    github = githubResult
                }
            });
        });
    }

    private static async Task<object> CheckAsync<T>(Func<Task<T>> probe) where T : class
    {
        try
        {
            return await probe() ?? (object)new { status = "unknown" };
        }
        catch (OperationCanceledException)
        {
            return new { status = "timeout" };
        }
        catch (Exception ex)
        {
            return new { status = "error", detail = ex.Message };
        }
    }

    private static bool IsOk(object? result)
    {
        if (result is null) return false;
        var prop = result.GetType().GetProperty("status");
        return (prop?.GetValue(result) as string) == "ok";
    }

    private static void MapJira(RouteGroupBuilder api)
    {
        api.MapPost("/jira/connection", async (
            JiraConnectionRequest request,
            IJiraConnectionProvider provider,
            IJiraClient jira,
            CancellationToken ct) =>
        {
            var connection = new JiraConnection
            {
                BaseUrl = request.BaseUrl,
                Email = request.Email,
                ApiTokenSecretKey = SecretKeys.JiraApiToken
            };
            var ok = await jira.TestConnectionAsync(connection, request.ApiToken, ct);
            if (!ok)
            {
                return Results.BadRequest(new { error = "Could not authenticate against Jira." });
            }
            await provider.SaveAsync(connection, request.ApiToken, ct);
            return Results.Ok(new { connection.BaseUrl, connection.Email });
        });

        api.MapGet("/jira/connection", async (IJiraConnectionProvider provider, CancellationToken ct) =>
        {
            var connection = await provider.GetAsync(ct);
            return connection is null
                ? Results.NotFound()
                : Results.Ok(new { connection.BaseUrl, connection.Email });
        });

        api.MapGet("/jira/active-board", async (IJiraConnectionProvider provider, CancellationToken ct) =>
        {
            var boardId = await provider.GetActiveBoardIdAsync(ct);
            return boardId is null ? Results.NotFound() : Results.Ok(new { boardId });
        });

        api.MapPut("/jira/active-board", async (
            ActiveBoardRequest request,
            IJiraConnectionProvider provider,
            CancellationToken ct) =>
        {
            await provider.SetActiveBoardIdAsync(request.BoardId, ct);
            return Results.NoContent();
        });

        api.MapPost("/github/pat", async (GitHubPatRequest request, ISecretStore secrets, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Token))
            {
                await secrets.RemoveAsync(SecretKeys.GitHubPat, ct);
            }
            else
            {
                await secrets.SetAsync(SecretKeys.GitHubPat, request.Token, ct);
            }
            return Results.NoContent();
        });

        api.MapGet("/github/pat/status", async (ISecretStore secrets, CancellationToken ct) =>
        {
            var pat = await secrets.GetAsync(SecretKeys.GitHubPat, ct);
            return Results.Ok(new { configured = !string.IsNullOrEmpty(pat) });
        });

        api.MapGet("/github/repos", async (IGitHubClient github, CancellationToken ct)
            => Results.Ok(await github.ListAccessibleReposAsync(ct)));

        api.MapGet("/boards", async (IJiraClient jira, CancellationToken ct)
            => Results.Ok(await jira.GetBoardsAsync(ct)));

        api.MapGet("/tickets", async (
            int boardId,
            string? jql,
            IJiraClient jira,
            CancellationToken ct) => Results.Ok(await jira.GetBoardIssuesAsync(boardId, jql, ct)));

        api.MapGet("/tickets/{key}", async (string key, IJiraClient jira, CancellationToken ct) =>
        {
            var ticket = await jira.GetIssueAsync(key, ct)
                ?? throw new KeyNotFoundException($"Ticket {key} not found.");
            return Results.Ok(ticket);
        });
    }

    private static void MapRepos(RouteGroupBuilder api)
    {
        api.MapGet("/repos", async (IRepositoryRegistry registry, CancellationToken ct)
            => Results.Ok(await registry.ListAsync(ct)));

        api.MapPost("/repos", async (Repository repo, IRepositoryRegistry registry, CancellationToken ct) =>
        {
            await registry.AddAsync(repo, ct);
            return Results.Created($"/api/repos/{repo.Name}", repo);
        });

        api.MapPut("/repos/{name}", async (string name, Repository repo, IRepositoryRegistry registry, CancellationToken ct) =>
        {
            if (!string.Equals(name, repo.Name, StringComparison.Ordinal))
            {
                return Results.Problem(
                    title: "Invalid request",
                    detail: "Repository name in the URL does not match the body.",
                    statusCode: StatusCodes.Status400BadRequest);
            }
            await registry.UpdateAsync(repo, ct);
            return Results.Ok(repo);
        });

        api.MapDelete("/repos/{name}", async (string name, IRepositoryRegistry registry, CancellationToken ct) =>
        {
            await registry.RemoveAsync(name, ct);
            return Results.NoContent();
        });
    }

    private static void MapWorkspaces(RouteGroupBuilder api)
    {
        api.MapGet("/workspaces", async (IWorkspaceManager manager, CancellationToken ct)
            => Results.Ok(await manager.ListAsync(ct)));

        api.MapGet("/workspaces/{key}", async (string key, IWorkspaceManager manager, CancellationToken ct) =>
        {
            var ws = await manager.GetAsync(key, ct)
                ?? throw new KeyNotFoundException($"Workspace {key} not found.");
            return Results.Ok(ws);
        });

        api.MapPost("/workspaces", async (
            CreateWorkspaceRequest request,
            IWorkspaceManager manager,
            CancellationToken ct) => Results.Ok(await manager.CreateAsync(request, ct)));

        api.MapPost("/workspaces/{key}/start", async (string key, IWorkspaceManager manager, CancellationToken ct) =>
        {
            await manager.StartAsync(key, ct);
            return Results.NoContent();
        });

        api.MapPost("/workspaces/{key}/stop", async (string key, IWorkspaceManager manager, CancellationToken ct) =>
        {
            await manager.StopAsync(key, ct);
            return Results.NoContent();
        });

        api.MapDelete("/workspaces/{key}", async (string key, IWorkspaceManager manager, CancellationToken ct) =>
        {
            await manager.DestroyAsync(key, ct);
            return Results.NoContent();
        });
    }
}

public record JiraConnectionRequest(string BaseUrl, string Email, string ApiToken);
public record ActiveBoardRequest(int? BoardId);
public record GitHubPatRequest(string? Token);
