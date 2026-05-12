using LightsOff.Core.Abstractions;
using LightsOff.Core.Configuration;
using LightsOff.Core.Domain;
using LightsOff.Core.Utilities;
using LightsOff.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LightsOff.Infrastructure.Demo;

/// <summary>
/// Demo workspace manager: persists the Workspace + repo links so the rest of the UI works,
/// but skips git clone, docker, and PR open. Submit returns canned PR URLs without touching the
/// real GitHub client.
/// </summary>
internal sealed class DemoWorkspaceManager : IWorkspaceManager
{
    private static readonly TimeSpan StepLatency = TimeSpan.FromMilliseconds(220);

    private readonly AppDbContext _db;
    private readonly LightsOffOptions _options;

    public DemoWorkspaceManager(AppDbContext db, IOptions<LightsOffOptions> options)
    {
        _db = db;
        _options = options.Value;
    }

    public async Task<Workspace> CreateAsync(CreateWorkspaceRequest request, CancellationToken ct = default)
    {
        if (request.RepositoryNames.Count == 0)
            throw new ArgumentException("At least one repository must be selected.", nameof(request));

        var existing = await _db.Workspaces.Include(w => w.Links)
            .FirstOrDefaultAsync(w => w.TicketKey == request.TicketKey, ct).ConfigureAwait(false);
        if (existing is not null)
            throw new InvalidOperationException($"Workspace for {request.TicketKey} already exists.");

        var branchName = request.BranchNameOverride
                         ?? TextSlug.BuildBranchName(_options.BranchNameTemplate, request.TicketKey, request.TicketSummary);

        // Pace the create call a touch so the UI's spinner registers.
        await Task.Delay(StepLatency, ct).ConfigureAwait(false);

        var workspaceRoot = Path.Combine(_options.WorkspacesRoot, request.TicketKey);

        var links = request.RepositoryNames
            .Select(name => new TicketRepositoryLink
            {
                TicketKey = request.TicketKey,
                RepositoryName = name,
                BranchName = branchName,
            })
            .ToList();

        var workspace = new Workspace
        {
            TicketKey = request.TicketKey,
            TicketSummary = request.TicketSummary,
            RootPath = workspaceRoot,
            ContainerId = "demo-container-01",
            ContainerName = $"lightsoff-{request.TicketKey.ToLowerInvariant()}",
            Status = request.StartContainer ? WorkspaceStatus.Running : WorkspaceStatus.Created,
            Links = links,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        _db.Workspaces.Add(workspace);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return workspace;
    }

    public async Task<IReadOnlyList<Workspace>> ListAsync(CancellationToken ct = default) =>
        await _db.Workspaces.Include(w => w.Links).ToListAsync(ct).ConfigureAwait(false);

    public async Task<Workspace?> GetAsync(string ticketKey, CancellationToken ct = default) =>
        await _db.Workspaces.Include(w => w.Links).FirstOrDefaultAsync(w => w.TicketKey == ticketKey, ct).ConfigureAwait(false);

    public async Task StartAsync(string ticketKey, CancellationToken ct = default)
    {
        var ws = await _db.Workspaces.FirstOrDefaultAsync(w => w.TicketKey == ticketKey, ct).ConfigureAwait(false);
        if (ws is null) return;
        ws.Status = WorkspaceStatus.Running;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task StopAsync(string ticketKey, CancellationToken ct = default)
    {
        var ws = await _db.Workspaces.FirstOrDefaultAsync(w => w.TicketKey == ticketKey, ct).ConfigureAwait(false);
        if (ws is null) return;
        ws.Status = WorkspaceStatus.Stopped;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task DestroyAsync(string ticketKey, CancellationToken ct = default)
    {
        var ws = await _db.Workspaces.Include(w => w.Links)
            .FirstOrDefaultAsync(w => w.TicketKey == ticketKey, ct).ConfigureAwait(false);
        if (ws is null) return;
        _db.Workspaces.Remove(ws);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PullRequestResult>> SubmitAsync(string ticketKey, CancellationToken ct = default)
    {
        await Task.Delay(StepLatency, ct).ConfigureAwait(false);
        var ws = await _db.Workspaces.Include(w => w.Links)
            .FirstOrDefaultAsync(w => w.TicketKey == ticketKey, ct).ConfigureAwait(false);
        if (ws is null) return Array.Empty<PullRequestResult>();

        var results = new List<PullRequestResult>();
        var prCounter = 41;
        foreach (var link in ws.Links)
        {
            var number = ++prCounter;
            results.Add(new PullRequestResult(
                RepositoryName: link.RepositoryName,
                BranchName: link.BranchName,
                Url: $"https://github.com/{DemoFixtures.DemoOwner}/{link.RepositoryName}/pull/{number}",
                Error: null,
                AlreadyExisted: false,
                Status: "open"));
        }
        return results;
    }
}
