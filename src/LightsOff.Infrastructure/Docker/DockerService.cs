using System.Runtime.InteropServices;
using Docker.DotNet;
using Docker.DotNet.Models;
using LightsOff.Core.Abstractions;
using Microsoft.Extensions.Logging;
using ContainerSpec = LightsOff.Core.Abstractions.ContainerSpec;

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

    public async Task<string> CreateWorkspaceContainerAsync(ContainerSpec spec, CancellationToken ct = default)
    {
        await EnsureImagePulledAsync(spec.Image, ct).ConfigureAwait(false);

        var existing = await TryFindContainerAsync(spec.Name, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            _logger.LogDebug("Container {Name} already exists ({Id})", spec.Name, existing.ID);
            return existing.ID;
        }

        var (exposed, portBindings) = BuildPortMaps(spec.Ports);

        var parameters = new CreateContainerParameters
        {
            Image = spec.Image,
            Name = spec.Name,
            Cmd = new List<string> { "sleep", "infinity" },
            Labels = new Dictionary<string, string> { ["lightsoff.managed"] = "true" },
            ExposedPorts = exposed,
            Env = spec.Env?.Select(kv => $"{kv.Key}={kv.Value}").ToList(),
            HostConfig = new HostConfig
            {
                Binds = spec.Binds.Select(b => $"{b.HostPath}:{b.ContainerPath}").ToList(),
                PortBindings = portBindings,
                AutoRemove = false
            },
            WorkingDir = spec.WorkingDir
        };

        var response = await _client.Value.Containers.CreateContainerAsync(parameters, ct).ConfigureAwait(false);
        _logger.LogInformation("Created container {Name} ({Id}) binds={BindCount} ports={PortCount} env={EnvCount}",
            spec.Name, response.ID, spec.Binds.Count, spec.Ports?.Count ?? 0, spec.Env?.Count ?? 0);
        return response.ID;
    }

    public async Task<(int ExitCode, string Output)> ExecAsync(string containerId, IReadOnlyList<string> command, CancellationToken ct = default)
    {
        var exec = await _client.Value.Exec.ExecCreateContainerAsync(containerId, new ContainerExecCreateParameters
        {
            Cmd = command.ToList(),
            AttachStdout = true,
            AttachStderr = true
        }, ct).ConfigureAwait(false);

        using var stream = await _client.Value.Exec.StartAndAttachContainerExecAsync(exec.ID, tty: false, ct)
            .ConfigureAwait(false);
        var (stdout, stderr) = await stream.ReadOutputToEndAsync(ct).ConfigureAwait(false);

        var inspect = await _client.Value.Exec.InspectContainerExecAsync(exec.ID, ct).ConfigureAwait(false);
        var combined = string.IsNullOrWhiteSpace(stderr) ? stdout : $"{stdout}\n{stderr}";
        if (inspect.ExitCode != 0)
        {
            _logger.LogWarning("Exec in {Id} exit={Exit}", containerId, inspect.ExitCode);
        }
        return ((int)inspect.ExitCode, combined.Trim());
    }

    public async Task<int> ExecStreamAsync(string containerId, IReadOnlyList<string> command, Func<string, Task> onChunk, CancellationToken ct = default)
    {
        var exec = await _client.Value.Exec.ExecCreateContainerAsync(containerId, new ContainerExecCreateParameters
        {
            Cmd = command.ToList(),
            AttachStdout = true,
            AttachStderr = true
        }, ct).ConfigureAwait(false);

        using var stream = await _client.Value.Exec.StartAndAttachContainerExecAsync(exec.ID, tty: false, ct)
            .ConfigureAwait(false);

        var buffer = new byte[4096];
        while (true)
        {
            var result = await stream.ReadOutputAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false);
            if (result.Count == 0) break;
            var text = System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count);
            await onChunk(text).ConfigureAwait(false);
        }

        var inspect = await _client.Value.Exec.InspectContainerExecAsync(exec.ID, ct).ConfigureAwait(false);
        return (int)inspect.ExitCode;
    }

    private static (Dictionary<string, EmptyStruct> Exposed, Dictionary<string, IList<PortBinding>> Bindings)
        BuildPortMaps(IReadOnlyList<string>? ports)
    {
        var exposed = new Dictionary<string, EmptyStruct>();
        var bindings = new Dictionary<string, IList<PortBinding>>();
        if (ports is null || ports.Count == 0)
        {
            return (exposed, bindings);
        }

        foreach (var raw in ports)
        {
            // Accept "3000", "3000:3000", "127.0.0.1:3000:3000"
            var parts = raw.Split(':', StringSplitOptions.TrimEntries);
            string host, container;
            string bindIp = "0.0.0.0";
            switch (parts.Length)
            {
                case 1:
                    host = container = parts[0];
                    break;
                case 2:
                    host = parts[0];
                    container = parts[1];
                    break;
                case 3:
                    bindIp = parts[0];
                    host = parts[1];
                    container = parts[2];
                    break;
                default:
                    continue;
            }

            var key = $"{container}/tcp";
            exposed[key] = default;
            if (!bindings.ContainsKey(key))
            {
                bindings[key] = new List<PortBinding>();
            }
            bindings[key].Add(new PortBinding { HostIP = bindIp, HostPort = host });
        }

        return (exposed, bindings);
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
