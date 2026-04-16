using System.Runtime.InteropServices;
using System.Text.Json;
using LightsOff.Core.Abstractions;
using LightsOff.Core.Configuration;
using Microsoft.Extensions.Options;

namespace LightsOff.Infrastructure.Secrets;

public class FileSecretStore : ISecretStore
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileSecretStore(IOptions<LightsOffOptions> options)
    {
        _filePath = Path.Combine(options.Value.StateRoot, "secrets.json");
    }

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        var secrets = await LoadAsync(ct).ConfigureAwait(false);
        return secrets.TryGetValue(key, out var value) ? value : null;
    }

    public async Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var secrets = await LoadUnguardedAsync(ct).ConfigureAwait(false);
            secrets[key] = value;
            await SaveUnguardedAsync(secrets, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveAsync(string key, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var secrets = await LoadUnguardedAsync(ct).ConfigureAwait(false);
            if (secrets.Remove(key))
            {
                await SaveUnguardedAsync(secrets, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Dictionary<string, string>> LoadAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await LoadUnguardedAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Dictionary<string, string>> LoadUnguardedAsync(CancellationToken ct)
    {
        if (!File.Exists(_filePath))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        await using var stream = File.OpenRead(_filePath);
        var data = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(stream, cancellationToken: ct)
                   .ConfigureAwait(false);
        return data ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private async Task SaveUnguardedAsync(Dictionary<string, string> secrets, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        await using (var stream = File.Create(_filePath))
        {
            await JsonSerializer.SerializeAsync(stream, secrets, new JsonSerializerOptions { WriteIndented = true }, ct)
                .ConfigureAwait(false);
        }
        TryRestrictPermissions(_filePath);
    }

    private static void TryRestrictPermissions(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
            // best-effort; ignore if filesystem doesn't support it
        }
    }
}
