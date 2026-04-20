using LightsOff.Core.Abstractions;
using LightsOff.Core.Domain;
using Microsoft.Extensions.Logging;

namespace LightsOff.Infrastructure.Agents;

/// <summary>
/// AI agent that uses the Anthropic (Claude) API for deep repo analysis, ticket understanding,
/// and code review. Requires <c>SecretKeys.ClaudeApiKey</c> to be configured.
///
/// Phase D shell — the interface is wired but all operations currently delegate to the local
/// agent for the analysis portion and will be replaced with actual Claude API calls when the
/// Anthropic SDK is integrated. The agent correctly reports <see cref="IsAvailable"/> = false
/// until an API key is configured, so the UI can show it grayed out.
/// </summary>
public class ClaudeCodeAgent : ICodeAgent
{
    public const string AgentId = "claude";

    private readonly ISecretStore _secrets;
    private readonly LocalCodeAgent _fallback;
    private readonly ILogger<ClaudeCodeAgent> _logger;

    public ClaudeCodeAgent(ISecretStore secrets, LocalCodeAgent fallback, ILogger<ClaudeCodeAgent> logger)
    {
        _secrets = secrets;
        _fallback = fallback;
        _logger = logger;
    }

    public string Id => AgentId;
    public string DisplayName => "Claude (Anthropic API)";
    public bool RequiresApiKey => true;

    public bool IsAvailable
    {
        get
        {
            var key = _secrets.GetAsync(SecretKeys.ClaudeApiKey).GetAwaiter().GetResult();
            return !string.IsNullOrWhiteSpace(key);
        }
    }

    public async Task<RepoContext> AnalyzeRepositoryAsync(string repoPath, RepoContext? existingContext = null, CancellationToken ct = default)
    {
        EnsureAvailable();
        // TODO: Send directory tree + key file contents to Claude via Anthropic SDK.
        // For now: delegate to local agent + tag the output with this agent's id.
        _logger.LogInformation("ClaudeCodeAgent.AnalyzeRepositoryAsync: falling back to local (API integration pending)");
        var ctx = await _fallback.AnalyzeRepositoryAsync(repoPath, existingContext, ct);
        ctx.AgentUsed = Id;
        return ctx;
    }

    public async Task<TicketAnalysis> AnalyzeTicketAsync(Ticket ticket, IReadOnlyList<RepoContext> repoContexts, CancellationToken ct = default)
    {
        EnsureAvailable();
        _logger.LogInformation("ClaudeCodeAgent.AnalyzeTicketAsync: falling back to local (API integration pending)");
        var analysis = await _fallback.AnalyzeTicketAsync(ticket, repoContexts, ct);
        analysis.AgentUsed = Id;
        return analysis;
    }

    public async Task<CodeReview> ReviewChangesAsync(string diff, RepoContext context, CancellationToken ct = default)
    {
        EnsureAvailable();
        _logger.LogInformation("ClaudeCodeAgent.ReviewChangesAsync: falling back to local (API integration pending)");
        var review = await _fallback.ReviewChangesAsync(diff, context, ct);
        review.AgentUsed = Id;
        return review;
    }

    public async Task<ImplementationPlan> GeneratePlanAsync(Ticket ticket, TicketAnalysis analysis, IReadOnlyList<RepoContext> repoContexts, CancellationToken ct = default)
    {
        EnsureAvailable();
        _logger.LogInformation("ClaudeCodeAgent.GeneratePlanAsync: falling back to local (API integration pending)");
        var plan = await _fallback.GeneratePlanAsync(ticket, analysis, repoContexts, ct);
        plan.AgentUsed = Id;
        return plan;
    }

    private void EnsureAvailable()
    {
        if (!IsAvailable)
        {
            throw new InvalidOperationException(
                "Claude API key is not configured. Add it in Settings under the 'Code Agent' section, or switch to the 'local' agent.");
        }
    }
}
