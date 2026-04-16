using LightsOff.Core.Abstractions;
using LightsOff.Core.Domain;
using LightsOff.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace LightsOff.Infrastructure.Registry;

public class RepositoryRegistry : IRepositoryRegistry
{
    private readonly AppDbContext _db;

    public RepositoryRegistry(AppDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<Repository>> ListAsync(CancellationToken ct = default)
        => await _db.Repositories.AsNoTracking().OrderBy(r => r.Name).ToListAsync(ct).ConfigureAwait(false);

    public Task<Repository?> GetAsync(string name, CancellationToken ct = default)
        => _db.Repositories.AsNoTracking().FirstOrDefaultAsync(r => r.Name == name, ct);

    public async Task AddAsync(Repository repo, CancellationToken ct = default)
    {
        _db.Repositories.Add(repo);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task UpdateAsync(Repository repo, CancellationToken ct = default)
    {
        var existing = await _db.Repositories.FindAsync(new object?[] { repo.Name }, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Repository '{repo.Name}' not found.");
        existing.CloneUrl = repo.CloneUrl;
        existing.DefaultBranch = repo.DefaultBranch;
        existing.Image = repo.Image;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task RemoveAsync(string name, CancellationToken ct = default)
    {
        var existing = await _db.Repositories.FindAsync(new object?[] { name }, ct).ConfigureAwait(false);
        if (existing is null)
        {
            return;
        }
        _db.Repositories.Remove(existing);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
