using LightsOff.Core.Domain;

namespace LightsOff.Core.Abstractions;

public interface IJiraClient
{
    Task<bool> TestConnectionAsync(JiraConnection connection, string apiToken, CancellationToken ct = default);
    Task<IReadOnlyList<Board>> GetBoardsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Ticket>> GetBoardIssuesAsync(int boardId, string? jql = null, CancellationToken ct = default);
    Task<Ticket?> GetIssueAsync(string key, CancellationToken ct = default);
}
