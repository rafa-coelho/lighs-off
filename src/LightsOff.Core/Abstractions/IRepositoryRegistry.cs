using LightsOff.Core.Domain;

namespace LightsOff.Core.Abstractions;

public interface IRepositoryRegistry
{
    Task<IReadOnlyList<Repository>> ListAsync(CancellationToken ct = default);
    Task<Repository?> GetAsync(string name, CancellationToken ct = default);
    Task AddAsync(Repository repo, CancellationToken ct = default);
    Task UpdateAsync(Repository repo, CancellationToken ct = default);
    Task RemoveAsync(string name, CancellationToken ct = default);
}
