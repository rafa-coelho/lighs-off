namespace LightsOff.Core.Domain;

/// <summary>
/// A natural-language user message in the unified ticket chat is classified into one of these
/// intents, which the UI maps onto a pipeline action. The chat has no slash-commands — the
/// <see cref="Abstractions.IIntentRouter"/> infers the intent from the message + current
/// <see cref="WorkflowState"/> (so it only proposes actions that are currently unlocked).
/// </summary>
public enum ChatIntent
{
    /// <summary>Plain conversation / a question for the agent. No pipeline action.</summary>
    Chat,

    /// <summary>(Re-)run ticket analysis against the repos.</summary>
    Analyze,

    /// <summary>Refine the current analysis with the user's clarification (non-destructive).</summary>
    RefinePlan,

    /// <summary>Produce a fresh plan that addresses the user's feedback. Resets to Draft.</summary>
    ReworkPlan,

    /// <summary>Approve the current plan so Implement unlocks.</summary>
    ApprovePlan,

    /// <summary>Answer to an open question / clarification batch posed by the agent.</summary>
    AnswerQuestion,

    /// <summary>Start the implementation session (writes code in the workspace).</summary>
    Implement,

    /// <summary>Run the configured test suite + coverage gates.</summary>
    RunTests,

    /// <summary>Commit, push and open the pull request(s).</summary>
    Submit,

    /// <summary>Fetch and summarize open review comments / findings on the PR.</summary>
    ReviewPr,

    /// <summary>Address PR review findings: reply, change code, and/or resolve threads.</summary>
    AddressPrFindings,

    /// <summary>Kick off the hands-off Implement → Validate → Submit chain.</summary>
    RunAutonomous,

    /// <summary>Cancel whatever step/chain is currently running.</summary>
    Cancel,
}

/// <summary>
/// Result of classifying a chat message. <see cref="Intent"/> is what the router believes the
/// user wants; <see cref="Confidence"/> lets the UI decide whether to act directly or confirm
/// first; <see cref="Rationale"/> is a short human-readable explanation surfaced in the chat;
/// <see cref="Payload"/> carries any extracted free-text (e.g. the feedback for a rework).
/// </summary>
public sealed record IntentClassification(
    ChatIntent Intent,
    double Confidence,
    string? Rationale = null,
    string? Payload = null);
