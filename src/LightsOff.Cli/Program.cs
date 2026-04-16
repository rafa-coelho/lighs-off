using System.CommandLine;
using LightsOff.Cli;
using LightsOff.Core.Abstractions;

const string DefaultApiUrl = "http://localhost:5186";

var apiOption = new Option<string>("--api")
{
    Description = "Base URL of the lights-off API",
    DefaultValueFactory = _ => Environment.GetEnvironmentVariable("LIGHTSOFF_API") ?? DefaultApiUrl
};

var root = new RootCommand("lights-off CLI");
root.Options.Add(apiOption);

root.Subcommands.Add(BuildBoardsCommand(apiOption));
root.Subcommands.Add(BuildTicketsCommand(apiOption));
root.Subcommands.Add(BuildReposCommand(apiOption));
root.Subcommands.Add(BuildWorkspaceCommand(apiOption));
root.Subcommands.Add(BuildSettingsCommand(apiOption));

return await root.Parse(args).InvokeAsync();

static Func<ParseResult, CancellationToken, Task<int>> Run(Func<ParseResult, CancellationToken, Task> body)
    => async (pr, ct) =>
    {
        try
        {
            await body(pr, ct);
            return 0;
        }
        catch (ApiException ex)
        {
            var label = ex.StatusCode switch
            {
                0 => "connection error",
                428 => "not configured",
                404 => "not found",
                400 => "bad request",
                _ when ex.StatusCode >= 500 => $"server error {ex.StatusCode}",
                _ => $"error {ex.StatusCode}"
            };
            Console.Error.WriteLine($"lights-off: {label}: {ex.Message}");
            return 1;
        }
    };

static Command BuildBoardsCommand(Option<string> apiOption)
{
    var list = new Command("list", "List Jira boards");
    list.SetAction(Run(async (parseResult, ct) =>
    {
        var api = ApiClient.Create(parseResult.GetValue(apiOption)!);
        var boards = await api.GetBoardsAsync(ct);
        if (boards.Count == 0)
        {
            Console.WriteLine("(no boards)");
        }
        foreach (var b in boards)
        {
            Console.WriteLine($"{b.Id,6}  {b.Name}  ({b.Type ?? "?"})");
        }
    }));

    var boards = new Command("boards", "Board commands");
    boards.Subcommands.Add(list);
    return boards;
}

static Command BuildTicketsCommand(Option<string> apiOption)
{
    var boardOption = new Option<int>("--board", "-b") { Required = true };
    var jqlOption = new Option<string?>("--jql");

    var list = new Command("list", "List tickets on a board")
    {
        boardOption, jqlOption
    };
    list.SetAction(Run(async (parseResult, ct) =>
    {
        var api = ApiClient.Create(parseResult.GetValue(apiOption)!);
        var tickets = await api.GetTicketsAsync(parseResult.GetValue(boardOption), parseResult.GetValue(jqlOption), ct);
        if (tickets.Count == 0)
        {
            Console.WriteLine("(no tickets)");
        }
        foreach (var t in tickets)
        {
            Console.WriteLine($"{t.Key,-12} [{t.Status,-12}] {t.Summary}");
        }
    }));

    var tickets = new Command("tickets", "Ticket commands");
    tickets.Subcommands.Add(list);
    return tickets;
}

static Command BuildReposCommand(Option<string> apiOption)
{
    var list = new Command("list", "List registered repositories");
    list.SetAction(Run(async (parseResult, ct) =>
    {
        var api = ApiClient.Create(parseResult.GetValue(apiOption)!);
        var repos = await api.GetReposAsync(ct);
        if (repos.Count == 0)
        {
            Console.WriteLine("(no repositories registered)");
        }
        foreach (var r in repos)
        {
            Console.WriteLine($"{r.Name,-30} {r.DefaultBranch,-15} {r.CloneUrl}");
        }
    }));

    var nameArg = new Argument<string>("name") { Description = "Repository name" };
    var urlOption = new Option<string>("--url", "-u") { Description = "Clone URL (https:// or git@…)", Required = true };
    var branchOption = new Option<string>("--branch", "-b")
    {
        Description = "Default branch",
        DefaultValueFactory = _ => "main"
    };
    var imageOption = new Option<string?>("--image") { Description = "Container image override" };

    var add = new Command("add", "Register a repository")
    {
        nameArg, urlOption, branchOption, imageOption
    };
    add.SetAction(Run(async (parseResult, ct) =>
    {
        var api = ApiClient.Create(parseResult.GetValue(apiOption)!);
        var repo = new LightsOff.Core.Domain.Repository
        {
            Name = parseResult.GetValue(nameArg)!,
            CloneUrl = parseResult.GetValue(urlOption)!,
            DefaultBranch = parseResult.GetValue(branchOption) ?? "main",
            Image = parseResult.GetValue(imageOption)
        };
        await api.AddRepoAsync(repo, ct);
        Console.WriteLine($"Added {repo.Name}.");
    }));

    var updateUrlOption = new Option<string?>("--url", "-u") { Description = "New clone URL" };
    var updateBranchOption = new Option<string?>("--branch", "-b") { Description = "New default branch" };
    var update = new Command("update", "Update a registered repository")
    {
        nameArg, updateUrlOption, updateBranchOption, imageOption
    };
    update.SetAction(Run(async (parseResult, ct) =>
    {
        var api = ApiClient.Create(parseResult.GetValue(apiOption)!);
        var name = parseResult.GetValue(nameArg)!;
        var existing = (await api.GetReposAsync(ct)).FirstOrDefault(r => r.Name == name)
            ?? throw new ApiException(404, $"Repository '{name}' not found.");
        var updated = new LightsOff.Core.Domain.Repository
        {
            Name = name,
            CloneUrl = parseResult.GetValue(updateUrlOption) ?? existing.CloneUrl,
            DefaultBranch = parseResult.GetValue(updateBranchOption) ?? existing.DefaultBranch,
            Image = parseResult.GetValue(imageOption) ?? existing.Image
        };
        await api.UpdateRepoAsync(updated, ct);
        Console.WriteLine($"Updated {name}.");
    }));

    var remove = new Command("remove", "Remove a registered repository") { nameArg };
    remove.SetAction(Run(async (parseResult, ct) =>
    {
        var api = ApiClient.Create(parseResult.GetValue(apiOption)!);
        await api.RemoveRepoAsync(parseResult.GetValue(nameArg)!, ct);
        Console.WriteLine($"Removed {parseResult.GetValue(nameArg)}.");
    }));

    var repos = new Command("repos", "Repository commands");
    repos.Subcommands.Add(list);
    repos.Subcommands.Add(add);
    repos.Subcommands.Add(update);
    repos.Subcommands.Add(remove);
    return repos;
}

static Command BuildWorkspaceCommand(Option<string> apiOption)
{
    var workspace = new Command("workspace", "Workspace commands");

    var list = new Command("list", "List workspaces");
    list.SetAction(Run(async (parseResult, ct) =>
    {
        var api = ApiClient.Create(parseResult.GetValue(apiOption)!);
        var workspaces = await api.GetWorkspacesAsync(ct);
        if (workspaces.Count == 0)
        {
            Console.WriteLine("(no workspaces)");
        }
        foreach (var w in workspaces)
        {
            var repos = string.Join(",", w.Links.Select(l => l.RepositoryName));
            Console.WriteLine($"{w.TicketKey,-12} {w.Status,-10} [{repos}] {w.ContainerName ?? "-"}");
        }
    }));

    var ticketArg = new Argument<string>("ticket") { Description = "Ticket key, e.g. PROJ-123" };
    var repoOption = new Option<string[]>("--repo", "-r")
    {
        Description = "Repository name to link (repeatable)",
        AllowMultipleArgumentsPerToken = true
    };
    var branchOption = new Option<string?>("--branch");
    var summaryOption = new Option<string?>("--summary");

    var create = new Command("create", "Create a workspace for a ticket")
    {
        ticketArg, repoOption, branchOption, summaryOption
    };
    create.SetAction(Run(async (parseResult, ct) =>
    {
        var api = ApiClient.Create(parseResult.GetValue(apiOption)!);
        var request = new CreateWorkspaceRequest(
            parseResult.GetValue(ticketArg)!,
            parseResult.GetValue(summaryOption) ?? string.Empty,
            parseResult.GetValue(repoOption) ?? Array.Empty<string>(),
            parseResult.GetValue(branchOption));
        var ws = await api.CreateWorkspaceAsync(request, ct);
        Console.WriteLine($"Created workspace for {ws.TicketKey} at {ws.RootPath}");
        if (!string.IsNullOrEmpty(ws.ContainerName))
        {
            Console.WriteLine($"Attach: docker exec -it {ws.ContainerName} bash");
        }
    }));

    var destroy = new Command("destroy", "Destroy a workspace") { ticketArg };
    destroy.SetAction(Run(async (parseResult, ct) =>
    {
        var api = ApiClient.Create(parseResult.GetValue(apiOption)!);
        await api.DestroyWorkspaceAsync(parseResult.GetValue(ticketArg)!, ct);
        Console.WriteLine($"Destroyed {parseResult.GetValue(ticketArg)}.");
    }));

    var start = new Command("start", "Start a workspace's container") { ticketArg };
    start.SetAction(Run(async (parseResult, ct) =>
    {
        var api = ApiClient.Create(parseResult.GetValue(apiOption)!);
        await api.StartWorkspaceAsync(parseResult.GetValue(ticketArg)!, ct);
        Console.WriteLine($"Started {parseResult.GetValue(ticketArg)}.");
    }));

    var stop = new Command("stop", "Stop a workspace's container") { ticketArg };
    stop.SetAction(Run(async (parseResult, ct) =>
    {
        var api = ApiClient.Create(parseResult.GetValue(apiOption)!);
        await api.StopWorkspaceAsync(parseResult.GetValue(ticketArg)!, ct);
        Console.WriteLine($"Stopped {parseResult.GetValue(ticketArg)}.");
    }));

    var attach = new Command("attach", "Print the docker exec command for a workspace") { ticketArg };
    attach.SetAction(Run(async (parseResult, ct) =>
    {
        var api = ApiClient.Create(parseResult.GetValue(apiOption)!);
        var ws = await api.GetWorkspaceAsync(parseResult.GetValue(ticketArg)!, ct);
        if (string.IsNullOrEmpty(ws.ContainerName))
        {
            throw new ApiException(428, $"Workspace {ws.TicketKey} has no container. Recreate it with Docker running to attach.");
        }
        // stdout: just the command so `$(lightsoff workspace attach KEY)` works in a shell.
        Console.WriteLine($"docker exec -it {ws.ContainerName} bash");
    }));

    workspace.Subcommands.Add(list);
    workspace.Subcommands.Add(create);
    workspace.Subcommands.Add(start);
    workspace.Subcommands.Add(stop);
    workspace.Subcommands.Add(attach);
    workspace.Subcommands.Add(destroy);
    return workspace;
}

static Command BuildSettingsCommand(Option<string> apiOption)
{
    var settings = new Command("settings", "Configure Jira + GitHub credentials");

    // Jira: lightsoff settings jira --base-url https://... --email you@ex.com --token ...
    var jiraBaseUrl = new Option<string>("--base-url", "-u") { Description = "Jira base URL", Required = true };
    var jiraEmail = new Option<string>("--email", "-e") { Description = "Atlassian account email", Required = true };
    var jiraToken = new Option<string>("--token", "-t") { Description = "Jira API token", Required = true };
    var jira = new Command("jira", "Save + test the Jira connection")
    {
        jiraBaseUrl, jiraEmail, jiraToken
    };
    jira.SetAction(Run(async (parseResult, ct) =>
    {
        var api = ApiClient.Create(parseResult.GetValue(apiOption)!);
        await api.SaveJiraConnectionAsync(
            parseResult.GetValue(jiraBaseUrl)!,
            parseResult.GetValue(jiraEmail)!,
            parseResult.GetValue(jiraToken)!,
            ct);
        Console.WriteLine("Jira connection saved.");
    }));

    // GitHub: lightsoff settings github --token ghp_... | --clear
    var ghToken = new Option<string?>("--token", "-t") { Description = "GitHub personal access token" };
    var ghClear = new Option<bool>("--clear") { Description = "Remove the stored GitHub PAT" };
    var github = new Command("github", "Save or clear the GitHub PAT")
    {
        ghToken, ghClear
    };
    github.SetAction(Run(async (parseResult, ct) =>
    {
        var api = ApiClient.Create(parseResult.GetValue(apiOption)!);
        if (parseResult.GetValue(ghClear))
        {
            await api.SaveGithubPatAsync(null, ct);
            Console.WriteLine("GitHub PAT removed.");
            return;
        }
        var token = parseResult.GetValue(ghToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ApiException(400, "Pass --token <pat> or --clear.");
        }
        await api.SaveGithubPatAsync(token, ct);
        Console.WriteLine("GitHub PAT saved.");
    }));

    settings.Subcommands.Add(jira);
    settings.Subcommands.Add(github);
    return settings;
}
