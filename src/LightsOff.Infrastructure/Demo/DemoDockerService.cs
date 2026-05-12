using LightsOff.Core.Abstractions;

namespace LightsOff.Infrastructure.Demo;

/// <summary>
/// No-op docker. Returns a synthetic container id so workspace records look real to the UI.
/// </summary>
internal sealed class DemoDockerService : IDockerService
{
    private const string DemoContainerId = "demo-container-01";

    public Task<string> CreateWorkspaceContainerAsync(ContainerSpec spec, CancellationToken ct = default)
        => Task.FromResult(DemoContainerId);

    public Task StartAsync(string containerId, CancellationToken ct = default) => Task.CompletedTask;
    public Task StopAsync(string containerId, CancellationToken ct = default) => Task.CompletedTask;
    public Task RemoveAsync(string containerId, CancellationToken ct = default) => Task.CompletedTask;
    public Task<string?> GetStatusAsync(string containerId, CancellationToken ct = default) => Task.FromResult<string?>("running");
    public Task<bool> IsDockerAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

    public Task<(int ExitCode, string Output)> ExecAsync(string containerId, IReadOnlyList<string> command, CancellationToken ct = default)
        => Task.FromResult((0, string.Empty));

    public Task<int> ExecStreamAsync(string containerId, IReadOnlyList<string> command, Func<string, Task> onChunk, CancellationToken ct = default)
        => Task.FromResult(0);
}
