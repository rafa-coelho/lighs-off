namespace LightsOff.Core.Abstractions;

public record WorkspaceBind(string HostPath, string ContainerPath);

public interface IDockerService
{
    Task<string> CreateWorkspaceContainerAsync(
        string containerName,
        string image,
        IReadOnlyList<WorkspaceBind> binds,
        string workingDir = "/workspace",
        CancellationToken ct = default);

    Task StartAsync(string containerId, CancellationToken ct = default);
    Task StopAsync(string containerId, CancellationToken ct = default);
    Task RemoveAsync(string containerId, CancellationToken ct = default);
    Task<string?> GetStatusAsync(string containerId, CancellationToken ct = default);
    Task<bool> IsDockerAvailableAsync(CancellationToken ct = default);
}
