using LightsOff.Core.Abstractions;
using LightsOff.Core.Domain;

namespace LightsOff.Infrastructure.Demo;

/// <summary>
/// Demo Jira: serves canned board + tickets with a small delay so the UI shows progress.
/// </summary>
internal sealed class DemoJiraClient : IJiraClient
{
    private static readonly TimeSpan Latency = TimeSpan.FromMilliseconds(180);

    public async Task<bool> TestConnectionAsync(JiraConnection connection, string apiToken, CancellationToken ct = default)
    {
        await Task.Delay(Latency, ct).ConfigureAwait(false);
        return true;
    }

    public async Task<IReadOnlyList<Board>> GetBoardsAsync(CancellationToken ct = default)
    {
        await Task.Delay(Latency, ct).ConfigureAwait(false);
        return DemoFixtures.Boards;
    }

    public async Task<IReadOnlyList<Ticket>> GetBoardIssuesAsync(int boardId, string? jql = null, CancellationToken ct = default)
    {
        await Task.Delay(Latency, ct).ConfigureAwait(false);
        return DemoFixtures.Tickets;
    }

    public async Task<Ticket?> GetIssueAsync(string key, CancellationToken ct = default)
    {
        await Task.Delay(Latency, ct).ConfigureAwait(false);
        return DemoFixtures.FindTicket(key);
    }
}
