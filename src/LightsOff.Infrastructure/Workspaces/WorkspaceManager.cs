using LightsOff.Core.Abstractions;
using LightsOff.Core.Configuration;
using LightsOff.Core.Domain;
using LightsOff.Core.Utilities;
using LightsOff.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LightsOff.Infrastructure.Workspaces;

public class WorkspaceManager : IWorkspaceManager
{
    private readonly AppDbContext _db;
    private readonly IRepositoryRegistry _repos;
    private readonly IGitService _git;
    private readonly IGitHubClient _github;
    private readonly IDockerService _docker;
    private readonly IJiraConnectionProvider _jiraConn;
    private readonly ICodeAgentProvider _agentProvider;
    private readonly RepoConfigLoader _configLoader;
    private readonly LightsOffOptions _options;
    private readonly ILogger<WorkspaceManager> _logger;

    public WorkspaceManager(
        AppDbContext db,
        IRepositoryRegistry repos,
        IGitService git,
        IGitHubClient github,
        IDockerService docker,
        IJiraConnectionProvider jiraConn,
        ICodeAgentProvider agentProvider,
        RepoConfigLoader configLoader,
        IOptions<LightsOffOptions> options,
        ILogger<WorkspaceManager> logger)
    {
        _db = db;
        _repos = repos;
        _git = git;
        _github = github;
        _docker = docker;
        _jiraConn = jiraConn;
        _agentProvider = agentProvider;
        _configLoader = configLoader;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<Workspace> CreateAsync(CreateWorkspaceRequest request, CancellationToken ct = default)
    {
        if (request.RepositoryNames.Count == 0)
        {
            throw new ArgumentException("At least one repository must be selected.", nameof(request));
        }

        var existing = await _db.Workspaces.Include(w => w.Links)
            .FirstOrDefaultAsync(w => w.TicketKey == request.TicketKey, ct)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            throw new InvalidOperationException($"Workspace for {request.TicketKey} already exists.");
        }

        var branchName = request.BranchNameOverride
                         ?? TextSlug.BuildBranchName(_options.BranchNameTemplate, request.TicketKey, request.TicketSummary);

        var workspaceRoot = Path.Combine(_options.WorkspacesRoot, request.TicketKey);
        Directory.CreateDirectory(workspaceRoot);

        var links = new List<TicketRepositoryLink>();
        var binds = new List<WorkspaceBind>();
        var resolvedRepos = new List<Repository>();
        var repoConfigs = new List<(string Name, RepoConfig? Config)>();
        Repository? firstRepo = null;
        foreach (var repoName in request.RepositoryNames)
        {
            var repo = await _repos.GetAsync(repoName, ct).ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"Repository '{repoName}' is not registered.");
            resolvedRepos.Add(repo);
            firstRepo ??= repo;

            var repoDestination = Path.Combine(_options.ReposRoot, repo.Name);
            await _git.EnsureClonedAsync(repo, repoDestination, ct).ConfigureAwait(false);
            await _git.FetchAndPullDefaultAsync(repo, repoDestination, ct).ConfigureAwait(false);
            await _git.CreateAndCheckoutBranchAsync(repoDestination, branchName, ct).ConfigureAwait(false);

            // Symlink on host so `ls ~/.lightsoff/workspaces/<key>/` shows the linked repos.
            // (Not useful inside the container — symlinks outside a bind don't resolve.)
            var linkPath = Path.Combine(workspaceRoot, repo.Name);
            EnsureSymlink(linkPath, repoDestination);

            // Bind mount the real repo directory into the container at /workspace/<name>
            // so it's directly browseable + editable from inside.
            binds.Add(new WorkspaceBind(repoDestination, $"/workspace/{repo.Name}"));

            // Optional per-repo config — affects image choice, ports, env, post-create steps.
            repoConfigs.Add((repo.Name, _configLoader.TryLoad(repoDestination)));

            // Analyze the repo and produce context.md for LLM/agent consumption.
            try
            {
                var agent = _agentProvider.GetDefaultAgent();
                await agent.AnalyzeRepositoryAsync(repoDestination, ct: ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Repo analysis failed for {Repo}; continuing without context", repo.Name);
            }

            links.Add(new TicketRepositoryLink
            {
                TicketKey = request.TicketKey,
                RepositoryName = repo.Name,
                BranchName = branchName
            });
        }

        var workspace = new Workspace
        {
            TicketKey = request.TicketKey,
            TicketSummary = request.TicketSummary,
            RootPath = workspaceRoot,
            Status = WorkspaceStatus.Created,
            Links = links,
            CreatedAt = DateTimeOffset.UtcNow
        };

        if (request.StartContainer)
        {
            if (await _docker.IsDockerAvailableAsync(ct).ConfigureAwait(false))
            {
                // Image precedence: .lightsoff.yaml > repo.Image override > global default. First-wins on conflict.
                var yamlImages = repoConfigs
                    .Select(rc => rc.Config?.Image)
                    .Where(i => !string.IsNullOrWhiteSpace(i))
                    .ToList();
                var repoImages = resolvedRepos
                    .Select(r => r.Image)
                    .Where(i => !string.IsNullOrWhiteSpace(i))
                    .ToList();
                var image = yamlImages.FirstOrDefault()
                         ?? repoImages.FirstOrDefault()
                         ?? _options.DefaultContainerImage;

                var allImages = yamlImages.Concat(repoImages).Distinct(StringComparer.Ordinal).ToList();
                if (allImages.Count > 1)
                {
                    _logger.LogWarning(
                        "Repositories linked to {TicketKey} disagree on container image ({Images}); using: {Chosen}",
                        request.TicketKey, string.Join(", ", allImages), image);
                }

                // Merge ports + env from every repo's .lightsoff.yaml.
                var ports = repoConfigs.SelectMany(rc => rc.Config?.Ports ?? Enumerable.Empty<string>()).ToList();
                var env = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var (_, config) in repoConfigs)
                {
                    if (config?.Env is null) continue;
                    foreach (var (k, v) in config.Env)
                    {
                        env[k] = v;
                    }
                }

                var containerName = $"lightsoff-{request.TicketKey.ToLowerInvariant()}";
                var spec = new ContainerSpec(containerName, image, binds, ports, env);
                var containerId = await _docker.CreateWorkspaceContainerAsync(spec, ct).ConfigureAwait(false);
                await _docker.StartAsync(containerId, ct).ConfigureAwait(false);
                workspace.ContainerId = containerId;
                workspace.ContainerName = containerName;
                workspace.Status = WorkspaceStatus.Running;

                // Auto-detect and install runtime dependencies for each repo
                var profileStore = new Data.DependencyProfileStore(_db);
                var depAgent = _agentProvider.GetDefaultAgent();
                foreach (var (repoName, _) in repoConfigs)
                {
                    try
                    {
                        var repoPath = Path.Combine(_options.ReposRoot, repoName);
                        var profile = await profileStore.LoadAsync(repoName, ct).ConfigureAwait(false);
                        if (profile is null)
                        {
                            profile = await depAgent.DetectDependenciesAsync(repoPath, ct).ConfigureAwait(false);
                            await profileStore.SaveAsync(profile, ct).ConfigureAwait(false);
                        }
                        if (profile.Runtimes.Count > 0 || profile.PackageInstallCommands.Count > 0 || profile.CustomCommands.Count > 0)
                        {
                            var script = Agents.RuntimeInstaller.GenerateInstallScript(profile);
                            _logger.LogInformation("Installing dependencies for {Repo}: {Runtimes}",
                                repoName, string.Join(", ", profile.Runtimes.Select(r => $"{r.Runtime}:{r.Version}")));
                            await _docker.ExecAsync(containerId, new[] { "sh", "-c", script }, ct).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Dependency install failed for {Repo}; container still running", repoName);
                    }
                }

                // Post-create commands run inside the container, scoped to each repo's working dir.
                foreach (var (name, config) in repoConfigs)
                {
                    if (config?.PostCreateCommands is not { Count: > 0 }) continue;
                    foreach (var cmd in config.PostCreateCommands)
                    {
                        _logger.LogInformation("Post-create ({Repo}): {Cmd}", name, cmd);
                        try
                        {
                            await _docker.ExecAsync(containerId,
                                new[] { "sh", "-c", $"cd /workspace/{name} && {cmd}" },
                                ct).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Post-create step failed for {Repo}: {Cmd}", name, cmd);
                        }
                    }
                }
            }
            else
            {
                _logger.LogWarning("Docker is not available; workspace created without a container.");
            }
        }

        _db.Workspaces.Add(workspace);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        return workspace;
    }

    public async Task<IReadOnlyList<Workspace>> ListAsync(CancellationToken ct = default)
        => await _db.Workspaces.Include(w => w.Links)
            .AsNoTracking()
            .OrderByDescending(w => w.CreatedAt)
            .ToListAsync(ct).ConfigureAwait(false);

    public Task<Workspace?> GetAsync(string ticketKey, CancellationToken ct = default)
        => _db.Workspaces.Include(w => w.Links)
            .AsNoTracking()
            .FirstOrDefaultAsync(w => w.TicketKey == ticketKey, ct);

    public async Task StartAsync(string ticketKey, CancellationToken ct = default)
    {
        var workspace = await _db.Workspaces.FirstOrDefaultAsync(w => w.TicketKey == ticketKey, ct)
            .ConfigureAwait(false) ?? throw new KeyNotFoundException($"Workspace {ticketKey} not found.");

        if (string.IsNullOrEmpty(workspace.ContainerId))
        {
            throw new InvalidOperationException(
                $"Workspace {ticketKey} has no container. Destroy and recreate it to start with Docker.");
        }

        await _docker.StartAsync(workspace.ContainerId, ct).ConfigureAwait(false);
        workspace.Status = WorkspaceStatus.Running;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task StopAsync(string ticketKey, CancellationToken ct = default)
    {
        var workspace = await _db.Workspaces.FirstOrDefaultAsync(w => w.TicketKey == ticketKey, ct)
            .ConfigureAwait(false) ?? throw new KeyNotFoundException($"Workspace {ticketKey} not found.");

        if (!string.IsNullOrEmpty(workspace.ContainerId))
        {
            await _docker.StopAsync(workspace.ContainerId, ct).ConfigureAwait(false);
        }
        workspace.Status = WorkspaceStatus.Stopped;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task DestroyAsync(string ticketKey, CancellationToken ct = default)
    {
        var workspace = await _db.Workspaces.Include(w => w.Links)
            .FirstOrDefaultAsync(w => w.TicketKey == ticketKey, ct).ConfigureAwait(false);
        if (workspace is null)
        {
            return;
        }

        if (!string.IsNullOrEmpty(workspace.ContainerId))
        {
            try
            {
                await _docker.RemoveAsync(workspace.ContainerId, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to remove container {Id}", workspace.ContainerId);
            }
        }

        if (Directory.Exists(workspace.RootPath))
        {
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(workspace.RootPath))
                {
                    var info = new FileInfo(entry);
                    if (info.LinkTarget is not null)
                    {
                        File.Delete(entry);
                    }
                }
                Directory.Delete(workspace.RootPath, recursive: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to clean workspace dir {Path}", workspace.RootPath);
            }
        }

        _db.Workspaces.Remove(workspace);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PullRequestResult>> SubmitAsync(string ticketKey, CancellationToken ct = default)
    {
        var workspace = await _db.Workspaces.Include(w => w.Links)
            .FirstOrDefaultAsync(w => w.TicketKey == ticketKey, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Workspace {ticketKey} not found.");

        if (workspace.Links.Count == 0)
        {
            throw new InvalidOperationException($"Workspace {ticketKey} has no linked repositories.");
        }

        var jira = await _jiraConn.GetAsync(ct).ConfigureAwait(false);
        var ticketUrl = jira is null
            ? null
            : $"{jira.BaseUrl.TrimEnd('/')}/browse/{ticketKey}";

        var title = $"{ticketKey}: {workspace.TicketSummary}";
        var body = ticketUrl is null
            ? $"Resolves {ticketKey}\n\n_Created via lights-off_"
            : $"Resolves {ticketKey}\n\n{ticketUrl}\n\n_Created via lights-off_";

        var results = new List<PullRequestResult>();
        foreach (var link in workspace.Links)
        {
            try
            {
                var repo = await _repos.GetAsync(link.RepositoryName, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException(
                        $"Repository '{link.RepositoryName}' was unregistered after workspace creation.");

                var repoPath = Path.Combine(_options.ReposRoot, link.RepositoryName);

                await _git.PushBranchAsync(repoPath, link.BranchName, ct).ConfigureAwait(false);

                var (owner, name) = ParseOwnerRepo(repo.CloneUrl);
                var prUrl = await _github.CreatePullRequestAsync(
                    owner, name, link.BranchName, repo.DefaultBranch, title, body, ct).ConfigureAwait(false);

                _logger.LogInformation("Opened PR {Url} for {Repo} on {Branch}", prUrl, link.RepositoryName, link.BranchName);
                results.Add(new PullRequestResult(link.RepositoryName, link.BranchName, prUrl, null));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Submit failed for {Repo}", link.RepositoryName);
                results.Add(new PullRequestResult(link.RepositoryName, link.BranchName, null, ex.Message));
            }
        }

        return results;
    }

    private static (string owner, string name) ParseOwnerRepo(string cloneUrl)
    {
        // Accept https://github.com/owner/repo(.git) and git@github.com:owner/repo(.git)
        var normalized = cloneUrl.Trim();
        if (normalized.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "https://github.com/" + normalized["git@github.com:".Length..];
        }
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
        {
            throw new FormatException($"Can't parse owner/repo from clone URL: {cloneUrl}");
        }
        var path = uri.AbsolutePath.TrimStart('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^4];
        }
        var parts = path.Split('/', 2);
        if (parts.Length < 2)
        {
            throw new FormatException($"Clone URL doesn't look like owner/repo: {cloneUrl}");
        }
        return (parts[0], parts[1]);
    }

    private static void EnsureSymlink(string linkPath, string target)
    {
        if (Directory.Exists(linkPath) || File.Exists(linkPath))
        {
            return;
        }
        Directory.CreateSymbolicLink(linkPath, target);
    }
}
