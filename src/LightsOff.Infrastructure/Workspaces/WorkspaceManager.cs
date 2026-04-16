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
    private readonly IDockerService _docker;
    private readonly LightsOffOptions _options;
    private readonly ILogger<WorkspaceManager> _logger;

    public WorkspaceManager(
        AppDbContext db,
        IRepositoryRegistry repos,
        IGitService git,
        IDockerService docker,
        IOptions<LightsOffOptions> options,
        ILogger<WorkspaceManager> logger)
    {
        _db = db;
        _repos = repos;
        _git = git;
        _docker = docker;
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
                var image = firstRepo?.Image ?? _options.DefaultContainerImage;

                // Warn if linked repos disagree on image override — we pick the first one.
                var distinctImages = resolvedRepos
                    .Select(r => r.Image)
                    .Where(i => !string.IsNullOrWhiteSpace(i))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                if (distinctImages.Count > 1)
                {
                    _logger.LogWarning(
                        "Repositories linked to {TicketKey} disagree on container image ({Images}); using first: {Chosen}",
                        request.TicketKey, string.Join(", ", distinctImages), image);
                }

                var containerName = $"lightsoff-{request.TicketKey.ToLowerInvariant()}";
                var containerId = await _docker.CreateWorkspaceContainerAsync(
                    containerName, image, binds, "/workspace", ct).ConfigureAwait(false);
                await _docker.StartAsync(containerId, ct).ConfigureAwait(false);
                workspace.ContainerId = containerId;
                workspace.ContainerName = containerName;
                workspace.Status = WorkspaceStatus.Running;
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

    private static void EnsureSymlink(string linkPath, string target)
    {
        if (Directory.Exists(linkPath) || File.Exists(linkPath))
        {
            return;
        }
        Directory.CreateSymbolicLink(linkPath, target);
    }
}
