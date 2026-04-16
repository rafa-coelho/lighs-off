using LightsOff.Core.Domain;

namespace LightsOff.Core.Abstractions;

public interface IJiraConnectionProvider
{
    Task<JiraConnection?> GetAsync(CancellationToken ct = default);
    Task SaveAsync(JiraConnection connection, string apiToken, CancellationToken ct = default);
    Task<int?> GetActiveBoardIdAsync(CancellationToken ct = default);
    Task SetActiveBoardIdAsync(int? boardId, CancellationToken ct = default);
}
