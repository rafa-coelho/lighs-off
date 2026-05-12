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
        IProgress<string>? progress = null,
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
        IProgress<string>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Start a streaming, stateful session for the agent to execute tasks (implement), re-review
    /// its own output, or chat with the user. The returned session stays alive until the caller
    /// disposes it (normal exit), cancels it, or its underlying process dies.
    ///
    /// Local agent: throws <see cref="NotSupportedException"/> — heuristics can't stream.
    /// AI agents (claude-cli, Anthropic API): spawn the conversation backend and bridge events.
    /// </summary>
    Task<IAgentSession> StartSessionAsync(
        AgentSessionSpec spec,
        CancellationToken ct = default);

    /// <summary>
    /// Measure coverage on code that is new relative to <paramref name="baseBranch"/>. The agent
    /// is responsible for invoking the right coverage tool (lcov/cobertura/jacoco/coverlet) given
    /// the repo's detected runtimes and reading the produced report.
    ///
    /// Local agent: parses existing coverage artifacts at common paths (coverage/lcov.info,
    /// coverage.xml, etc.) and diffs against the base branch. Returns
    /// <see cref="CoverageReport.Error"/> set if no artifact is present — does not run tests itself.
    /// AI agents: may drive the coverage tool end-to-end via a session.
    /// </summary>
    Task<CoverageReport> MeasureNewCodeCoverageAsync(
        string repoPath,
        string baseBranch,
        CancellationToken ct = default);

    /// <summary>
    /// Single-turn reply to a user chat message in the context of an existing plan (used for
    /// Plan-step replan discussion). Not a full session — one question in, one reply out.
    ///
    /// Local agent: throws <see cref="NotSupportedException"/>.
    /// AI agents: call the backend with plan + message and return the reply as a
    /// <see cref="ChatReplyEvent"/>.
    /// </summary>
    Task<ChatReplyEvent> ReplyToPlanChatAsync(
        ImplementationPlan plan,
        string userMessage,
        CancellationToken ct = default);

    /// <summary>
    /// Produce a fresh implementation plan by reworking <paramref name="previousPlan"/> in light
    /// of <paramref name="userFeedback"/>. The returned plan is a complete replacement — not a
    /// patch — and starts in <see cref="PlanStatus.Draft"/> so the user re-approves.
    ///
    /// Local agent: throws <see cref="NotSupportedException"/> — heuristics can't act on free-form feedback.
    /// AI agents: include the previous plan + the full comment chain in the prompt so the model
    /// can address the feedback directly without the user repeating context.
    /// </summary>
    Task<ImplementationPlan> ReworkPlanAsync(
        Ticket ticket,
        ImplementationPlan previousPlan,
        string userFeedback,
        IReadOnlyList<RepoContext> repoContexts,
        CancellationToken ct = default);

    /// <summary>
    /// Assess whether <paramref name="diff"/> actually satisfies the ticket's acceptance
    /// criteria. Returns a <see cref="AcReviewReport"/> with a verdict + specific concerns.
    /// Output is consumed as a warning, not a gate — AC text is too often vague for an AI
    /// verdict to be a reliable block.
    ///
    /// Local agent: throws <see cref="NotSupportedException"/> — heuristics can't read AC text.
    /// AI agents: send the ticket + diff and parse the structured response.
    /// </summary>
    Task<AcReviewReport> ReviewAgainstAcAsync(
        Ticket ticket,
        string diff,
        RepoContext context,
        CancellationToken ct = default);

    /// <summary>
    /// Refresh a ticket analysis after the user has answered open questions or provided
    /// clarifications via chat. The full <paramref name="conversation"/> (chronological,
    /// including the message that triggered this refinement) is included in the prompt so
    /// the agent can address the latest input directly.
    ///
    /// Local agent: throws <see cref="NotSupportedException"/>.
    /// AI agents: re-call analysis with the conversation as additional context.
    /// </summary>
    Task<TicketAnalysis> RefineAnalysisAsync(
        Ticket ticket,
        TicketAnalysis previousAnalysis,
        IReadOnlyList<ChatMessage> conversation,
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
