namespace LightsOff.Core.Domain;

/// <summary>
/// Declarative spec passed to <c>ICodeAgent.StartSessionAsync</c> — everything a streaming, stateful
/// agent session needs to run a pipeline step: the ticket, optional approved plan, which repo
/// paths it's allowed to touch, which container owns the runtime (for test/build orchestration
/// by the runner, not the agent itself), and an optional prior session id if this is a resume.
/// </summary>
public sealed record AgentSessionSpec(
    string TicketKey,
    Ticket Ticket,
    ImplementationPlan? Plan,
    IReadOnlyList<RepoContext> RepoContexts,
    string WorkspaceRoot,
    IReadOnlyList<string> AllowedRepoPaths,
    string? ContainerId,
    AgentSessionMode Mode,
    string? PreviousSessionId = null);

public enum AgentSessionMode { Implement, Validate, Review, PlanChat }

// ── Inputs the caller sends into an active session ───────────────────────────

public abstract record AgentInput;

/// <summary>Start or continue executing a discrete task from the approved plan.</summary>
public sealed record AgentTaskInput(string Instruction, IReadOnlyList<string> AllowedPaths) : AgentInput;

/// <summary>Free-form user message delivered at the next agent turn (chat during run, replan).</summary>
public sealed record AgentChatInput(string Message) : AgentInput;

/// <summary>Answer to a <see cref="ClarifyingQuestionEvent"/> the agent previously raised.</summary>
public sealed record AgentAnswerInput(string QuestionId, string Answer) : AgentInput;

// ── Events the session emits back to the runner ──────────────────────────────

public abstract record AgentEvent(DateTimeOffset At);

/// <summary>A raw chunk of stdout or stderr. Stream is "stdout" or "stderr".</summary>
public sealed record LogChunkEvent(DateTimeOffset At, string Stream, string Text) : AgentEvent(At);

/// <summary>The agent invoked a tool (bash, edit, read, etc.). Args are JSON.</summary>
public sealed record ToolCallEvent(DateTimeOffset At, string Tool, string ArgsJson) : AgentEvent(At);

/// <summary>A (possibly streaming) chat reply. Non-final chunks accumulate; IsFinal marks the last.</summary>
public sealed record ChatReplyEvent(DateTimeOffset At, string Text, bool IsFinal) : AgentEvent(At);

/// <summary>Agent needs a blocking answer from the user before proceeding.</summary>
public sealed record ClarifyingQuestionEvent(DateTimeOffset At, string Id, string Text) : AgentEvent(At);

/// <summary>The session ended. Success + optional error message. Always the final event.</summary>
public sealed record SessionFinishedEvent(DateTimeOffset At, bool Success, string? Error) : AgentEvent(At);
