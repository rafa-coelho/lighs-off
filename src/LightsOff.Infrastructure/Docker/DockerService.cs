using System.Runtime.InteropServices;
using Docker.DotNet;
using Docker.DotNet.Models;
using LightsOff.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace LightsOff.Infrastructure.Docker;

public class DockerService : IDockerService, IAsyncDisposable
{
    private readonly ILogger<DockerService> _logger;
    private readonly Lazy<DockerClient> _client;

    public DockerService(ILogger<DockerService> logger)
    {
        _logger = logger;
        _client = new Lazy<DockerClient>(CreateClient);
    }

    public async Task<string> CreateWorkspaceContainerAsync(
        string containerName,
        string image,
        IReadOnlyList<WorkspaceBind> binds,
        string workingDir = "/workspace",
        CancellationToken ct = default)
    {
        await EnsureImagePulledAsync(image, ct).ConfigureAwait(false);

        var existing = await TryFindContainerAsync(containerName, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            _logger.LogDebug("Container {Name} already exists ({Id})", containerName, existing.ID);
            return existing.ID;
        }

        var parameters = new CreateContainerParameters
        {
            Image = image,
            Name = containerName,
            Cmd = new List<string> { "sleep", "infinity" },
            Labels = new Dictionary<string, string> { ["lightsoff.managed"] = "true" },
            HostConfig = new HostConfig
            {
                Binds = binds.Select(b => $"{b.HostPath}:{b.ContainerPath}").ToList(),
                AutoRemove = false
            },
            WorkingDir = workingDir
        };

        var response = await _client.Value.Containers.CreateContainerAsync(parameters, ct).ConfigureAwait(false);
        _logger.LogInformation("Created container {Name} with id {Id} and {BindCount} bind(s)",
            containerName, response.ID, binds.Count);
        return response.ID;
    }

    public async Task StartAsync(string containerId, CancellationToken ct = default)
    {
        await _client.Value.Containers.StartContainerAsync(containerId, new ContainerStartParameters(), ct)
            .ConfigureAwait(false);
    }

    public async Task StopAsync(string containerId, CancellationToken ct = default)
    {
        await _client.Value.Containers.StopContainerAsync(
            containerId,
            new ContainerStopParameters { WaitBeforeKillSeconds = 5 },
            ct).ConfigureAwait(false);
    }

    public async Task RemoveAsync(string containerId, CancellationToken ct = default)
    {
        await _client.Value.Containers.RemoveContainerAsync(
            containerId,
            new ContainerRemoveParameters { Force = true },
            ct).ConfigureAwait(false);
    }

    public async Task<string?> GetStatusAsync(string containerId, CancellationToken ct = default)
    {
        try
        {
            var response = await _client.Value.Containers.InspectContainerAsync(containerId, ct).ConfigureAwait(false);
            return response.State?.Status;
        }
        catch (DockerContainerNotFoundException)
        {
            return null;
        }
    }

    public async Task<bool> IsDockerAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            await _client.Value.System.PingAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Docker daemon unreachable");
            return false;
        }
    }

    private async Task EnsureImagePulledAsync(string image, CancellationToken ct)
    {
        var images = await _client.Value.Images.ListImagesAsync(
            new ImagesListParameters { Filters = new Dictionary<string, IDictionary<string, bool>>
            {
                ["reference"] = new Dictionary<string, bool> { [image] = true }
            }},
            ct).ConfigureAwait(false);

        if (images.Count > 0)
        {
            return;
        }

        _logger.LogInformation("Pulling image {Image}...", image);
        await _client.Value.Images.CreateImageAsync(
            new ImagesCreateParameters { FromImage = image },
            authConfig: null,
            progress: new Progress<JSONMessage>(),
            cancellationToken: ct).ConfigureAwait(false);
    }

    private async Task<ContainerListResponse?> TryFindContainerAsync(string name, CancellationToken ct)
    {
        var all = await _client.Value.Containers.ListContainersAsync(new ContainersListParameters
        {
            All = true,
            Filters = new Dictionary<string, IDictionary<string, bool>>
            {
                ["name"] = new Dictionary<string, bool> { [name] = true }
            }
        }, ct).ConfigureAwait(false);
        return all.FirstOrDefault(c => c.Names.Any(n => n.TrimStart('/') == name));
    }

    private static DockerClient CreateClient()
    {
        var defaultSocket = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "npipe://./pipe/docker_engine"
            : "unix:///var/run/docker.sock";
        return new DockerClientConfiguration(new Uri(defaultSocket)).CreateClient();
    }

    public ValueTask DisposeAsync()
    {
        if (_client.IsValueCreated)
        {
            _client.Value.Dispose();
        }
        return ValueTask.CompletedTask;
    }
}
