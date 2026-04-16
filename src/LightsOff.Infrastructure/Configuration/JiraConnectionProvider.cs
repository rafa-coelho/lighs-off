using LightsOff.Core.Abstractions;
using LightsOff.Core.Domain;
using LightsOff.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace LightsOff.Infrastructure.Configuration;

public class JiraConnectionProvider : IJiraConnectionProvider
{
    private readonly AppDbContext _db;
    private readonly ISecretStore _secrets;

    public JiraConnectionProvider(AppDbContext db, ISecretStore secrets)
    {
        _db = db;
        _secrets = secrets;
    }

    public async Task<JiraConnection?> GetAsync(CancellationToken ct = default)
    {
        var settings = await LoadSettingsAsync(ct).ConfigureAwait(false);
        if (!settings.TryGetValue(AppSettingKeys.JiraBaseUrl, out var baseUrl) || string.IsNullOrWhiteSpace(baseUrl))
        {
            return null;
        }

        return new JiraConnection
        {
            BaseUrl = baseUrl,
            Email = settings.GetValueOrDefault(AppSettingKeys.JiraEmail) ?? string.Empty,
            ApiTokenSecretKey = settings.GetValueOrDefault(AppSettingKeys.JiraApiTokenSecretKey)
                                ?? SecretKeys.JiraApiToken
        };
    }

    public async Task SaveAsync(JiraConnection connection, string apiToken, CancellationToken ct = default)
    {
        await UpsertAsync(AppSettingKeys.JiraBaseUrl, connection.BaseUrl, ct).ConfigureAwait(false);
        await UpsertAsync(AppSettingKeys.JiraEmail, connection.Email, ct).ConfigureAwait(false);
        await UpsertAsync(AppSettingKeys.JiraApiTokenSecretKey, connection.ApiTokenSecretKey, ct).ConfigureAwait(false);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await _secrets.SetAsync(connection.ApiTokenSecretKey, apiToken, ct).ConfigureAwait(false);
    }

    public async Task<int?> GetActiveBoardIdAsync(CancellationToken ct = default)
    {
        var setting = await _db.AppSettings.FindAsync(new object?[] { AppSettingKeys.ActiveBoardId }, ct)
            .ConfigureAwait(false);
        return int.TryParse(setting?.Value, out var id) ? id : null;
    }

    public async Task SetActiveBoardIdAsync(int? boardId, CancellationToken ct = default)
    {
        await UpsertAsync(AppSettingKeys.ActiveBoardId, boardId?.ToString(), ct).ConfigureAwait(false);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private async Task<Dictionary<string, string?>> LoadSettingsAsync(CancellationToken ct)
    {
        var keys = new[]
        {
            AppSettingKeys.JiraBaseUrl,
            AppSettingKeys.JiraEmail,
            AppSettingKeys.JiraApiTokenSecretKey
        };
        var rows = await _db.AppSettings.Where(s => keys.Contains(s.Key)).ToListAsync(ct).ConfigureAwait(false);
        return rows.ToDictionary(s => s.Key, s => s.Value, StringComparer.Ordinal);
    }

    private async Task UpsertAsync(string key, string? value, CancellationToken ct)
    {
        var existing = await _db.AppSettings.FindAsync(new object?[] { key }, ct).ConfigureAwait(false);
        if (existing is null)
        {
            _db.AppSettings.Add(new AppSetting { Key = key, Value = value });
        }
        else
        {
            existing.Value = value;
        }
    }
}
