using LightsOff.Core.Domain;

namespace LightsOff.Core.Abstractions;

/// <summary>
/// A stateful, streaming conversation with an agent backend. Used for Implement/Validate/Review
/// steps and plan-replan chat — anything where the agent needs to take multiple turns, receive
/// mid-flight user input, and be pausable/cancellable.
///
/// Stateless one-shot operations (analyze, generate-plan, review-diff) stay on <see cref="ICodeAgent"/>.
/// </summary>
public interface IAgentSession : IAsyncDisposable
{
    string SessionId { get; }
    string AgentId { get; }

    /// <summary>Live event stream. Terminates with a single <see cref="SessionFinishedEvent"/>.</summary>
    IAsyncEnumerable<AgentEvent> Events { get; }

    /// <summary>
    /// Deliver input to the agent. Safe to call while the agent is mid-tool-call — the input is
    /// queued and picked up at the next turn. Task completes when the input is accepted, not when
    /// the agent has processed it.
    /// </summary>
    Task SendAsync(AgentInput input, CancellationToken ct = default);

    /// <summary>SIGSTOP the underlying process; no further events until <see cref="ResumeAsync"/>.</summary>
    Task PauseAsync(CancellationToken ct = default);

    /// <summary>SIGCONT the paused process.</summary>
    Task ResumeAsync(CancellationToken ct = default);

    /// <summary>SIGTERM with grace period then SIGKILL. Irreversible; <see cref="Events"/> terminates.</summary>
    Task CancelAsync(CancellationToken ct = default);
}
