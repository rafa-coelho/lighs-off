using LightsOff.Core.Abstractions;
using LightsOff.Core.Domain;
using LightsOff.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace LightsOff.App.Workers;

public class WorkspaceHealthWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<WorkspaceHealthWorker> _logger;
    private readonly TimeSpan _interval = TimeSpan.FromMinutes(1);

    public WorkspaceHealthWorker(IServiceScopeFactory scopeFactory, ILogger<WorkspaceHealthWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Workspace health tick failed");
            }

            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var docker = scope.ServiceProvider.GetRequiredService<IDockerService>();

        if (!await docker.IsDockerAvailableAsync(ct))
        {
            return;
        }

        var workspaces = await db.Workspaces
            .Where(w => w.Status != WorkspaceStatus.Destroyed && w.ContainerId != null)
            .ToListAsync(ct);

        foreach (var workspace in workspaces)
        {
            var status = await docker.GetStatusAsync(workspace.ContainerId!, ct);
            var desired = status switch
            {
                null => WorkspaceStatus.Destroyed,
                "running" => WorkspaceStatus.Running,
                _ => WorkspaceStatus.Stopped
            };
            if (workspace.Status != desired)
            {
                workspace.Status = desired;
            }
        }

        await db.SaveChangesAsync(ct);
    }
}
