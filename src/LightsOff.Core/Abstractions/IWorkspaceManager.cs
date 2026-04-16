using LightsOff.Core.Domain;

namespace LightsOff.Core.Abstractions;

public record CreateWorkspaceRequest(
    string TicketKey,
    string TicketSummary,
    IReadOnlyList<string> RepositoryNames,
    string? BranchNameOverride = null,
    bool StartContainer = true);

public record PullRequestResult(string RepositoryName, string BranchName, string? Url, string? Error);

public interface IWorkspaceManager
{
    Task<Workspace> CreateAsync(CreateWorkspaceRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<Workspace>> ListAsync(CancellationToken ct = default);
    Task<Workspace?> GetAsync(string ticketKey, CancellationToken ct = default);
    Task StartAsync(string ticketKey, CancellationToken ct = default);
    Task StopAsync(string ticketKey, CancellationToken ct = default);
    Task DestroyAsync(string ticketKey, CancellationToken ct = default);

    /// <summary>
    /// Pushes the ticket's feature branch from every linked repo and opens a pull request
    /// against the repo's default branch. Returns one result per linked repo; individual failures
    /// don't abort the whole batch.
    /// </summary>
    Task<IReadOnlyList<PullRequestResult>> SubmitAsync(string ticketKey, CancellationToken ct = default);
}
