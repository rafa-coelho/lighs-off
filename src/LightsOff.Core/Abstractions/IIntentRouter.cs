using LightsOff.Core.Domain;

namespace LightsOff.Core.Abstractions;

/// <summary>
/// Classifies a free-text chat message from the unified ticket chat into a <see cref="ChatIntent"/>.
/// This is what lets the single chat surface drive the whole pipeline without slash-commands: the
/// user types naturally ("now build it", "rode os testes", "open the PR") and the router maps that
/// onto the correct pipeline action, constrained by what the current <see cref="WorkflowState"/>
/// actually allows.
/// </summary>
public interface IIntentRouter
{
    /// <summary>
    /// Classify <paramref name="message"/> given the live <paramref name="state"/> (so the router
    /// won't propose, e.g., Implement before the plan is approved) and whether a clarification
    /// batch is currently open (which biases ambiguous messages toward
    /// <see cref="ChatIntent.AnswerQuestion"/>).
    /// </summary>
    Task<IntentClassification> ClassifyAsync(
        string message,
        WorkflowState state,
        bool hasOpenQuestions,
        CancellationToken ct = default);
}
