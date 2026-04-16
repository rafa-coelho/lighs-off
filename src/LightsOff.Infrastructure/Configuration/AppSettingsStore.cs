using LightsOff.Core.Abstractions;
using LightsOff.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace LightsOff.Infrastructure.Configuration;

public class AppSettingsStore : IAppSettingsStore
{
    private readonly AppDbContext _db;

    public AppSettingsStore(AppDbContext db)
    {
        _db = db;
    }

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        var row = await _db.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key, ct).ConfigureAwait(false);
        return row?.Value;
    }

    public async Task SetAsync(string key, string? value, CancellationToken ct = default)
    {
        var row = await _db.AppSettings.FindAsync(new object?[] { key }, ct).ConfigureAwait(false);
        if (row is null)
        {
            _db.AppSettings.Add(new AppSetting { Key = key, Value = value });
        }
        else
        {
            row.Value = value;
        }
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
