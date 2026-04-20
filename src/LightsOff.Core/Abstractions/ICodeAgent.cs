using LightsOff.Core.Domain;

namespace LightsOff.Core.Abstractions;

/// <summary>
/// Pluggable agent interface for all intelligent operations — repo analysis, ticket analysis,
/// code review, and (future) implementation. The local agent does file-system + rules;
/// AI agents (Claude, Qwen, Copilot) call their respective APIs through the same interface.
/// </summary>
public interface ICodeAgent
{
    string Id { get; }
    string DisplayName { get; }
    bool RequiresApiKey { get; }
    bool IsAvailable { get; }

    /// <summary>
    /// Deep analysis of a repository: tech stack, features, data flow, architecture.
    /// Local agent: file-system scan + pattern matching.
    /// AI agent: sends key files to the LLM for comprehensive understanding.
    /// </summary>
    Task<RepoContext> AnalyzeRepositoryAsync(
        string repoPath,
        RepoContext? existingContext = null,
        CancellationToken ct = default);

    /// <summary>
    /// Analyze a ticket against repo context: task breakdown, gap detection, relevant files.
    /// Local agent: rules-based heuristics.
    /// AI agent: LLM understands requirements and cross-references with codebase.
    /// </summary>
    Task<TicketAnalysis> AnalyzeTicketAsync(
        Ticket ticket,
        IReadOnlyList<RepoContext> repoContexts,
        CancellationToken ct = default);

    /// <summary>
    /// Review code changes for quality, security, and correctness.
    /// Local agent: regex patterns for common vulnerabilities.
    /// AI agent: LLM reads the diff + context and provides structured feedback.
    /// </summary>
    Task<CodeReview> ReviewChangesAsync(
        string diff,
        RepoContext context,
        CancellationToken ct = default);

    /// <summary>
    /// Detect runtime dependencies the repo needs to build and test.
    /// Local agent: file-system scan for package managers, version files, manifests.
    /// AI agent: reads config files and infers the full dependency tree.
    /// </summary>
    Task<RepoDependencyProfile> DetectDependenciesAsync(
        string repoPath,
        CancellationToken ct = default);

    /// <summary>
    /// Generate an implementation plan from a ticket analysis.
    /// Local agent: builds mechanically from task breakdown + relevant files.
    /// AI agent: produces a detailed approach, file-by-file.
    /// </summary>
    Task<ImplementationPlan> GeneratePlanAsync(
        Ticket ticket,
        TicketAnalysis analysis,
        IReadOnlyList<RepoContext> repoContexts,
        CancellationToken ct = default);
}

/// <summary>
/// Provides the configured agent instance. Reads <c>agent.default</c> from settings
/// and resolves the matching <see cref="ICodeAgent"/>.
/// </summary>
public interface ICodeAgentProvider
{
    ICodeAgent GetAgent(string? agentId = null);
    ICodeAgent GetDefaultAgent();
    IReadOnlyList<ICodeAgent> AvailableAgents { get; }
}
