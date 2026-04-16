using LightsOff.Core.Domain;

namespace LightsOff.Core.Abstractions;

public record CreateWorkspaceRequest(
    string TicketKey,
    string TicketSummary,
    IReadOnlyList<string> RepositoryNames,
    string? BranchNameOverride = null,
    bool StartContainer = true);

public interface IWorkspaceManager
{
    Task<Workspace> CreateAsync(CreateWorkspaceRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<Workspace>> ListAsync(CancellationToken ct = default);
    Task<Workspace?> GetAsync(string ticketKey, CancellationToken ct = default);
    Task StartAsync(string ticketKey, CancellationToken ct = default);
    Task StopAsync(string ticketKey, CancellationToken ct = default);
    Task DestroyAsync(string ticketKey, CancellationToken ct = default);
}
