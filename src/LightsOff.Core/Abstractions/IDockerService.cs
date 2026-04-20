namespace LightsOff.Core.Abstractions;

public record WorkspaceBind(string HostPath, string ContainerPath);

public record ContainerSpec(
    string Name,
    string Image,
    IReadOnlyList<WorkspaceBind> Binds,
    IReadOnlyList<string>? Ports = null,
    IReadOnlyDictionary<string, string>? Env = null,
    string WorkingDir = "/workspace");

public interface IDockerService
{
    Task<string> CreateWorkspaceContainerAsync(ContainerSpec spec, CancellationToken ct = default);

    Task StartAsync(string containerId, CancellationToken ct = default);
    Task StopAsync(string containerId, CancellationToken ct = default);
    Task RemoveAsync(string containerId, CancellationToken ct = default);
    Task<string?> GetStatusAsync(string containerId, CancellationToken ct = default);
    Task<bool> IsDockerAvailableAsync(CancellationToken ct = default);

    /// <summary>Run a shell command inside the container. Returns exit code + combined stdout/stderr output.</summary>
    Task<(int ExitCode, string Output)> ExecAsync(string containerId, IReadOnlyList<string> command, CancellationToken ct = default);

    /// <summary>Run a command with streaming output. <paramref name="onChunk"/> is called with each output chunk as it arrives.</summary>
    Task<int> ExecStreamAsync(string containerId, IReadOnlyList<string> command, Func<string, Task> onChunk, CancellationToken ct = default);
}
