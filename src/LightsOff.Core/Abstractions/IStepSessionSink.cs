using LightsOff.Core.Domain;

namespace LightsOff.Core.Abstractions;

/// <summary>
/// Lets the pipeline orchestrator (Infrastructure) hand a live <see cref="IAgentSession"/> back
/// to the step runner (App) so user-initiated chat/answer inputs and cancellation can be routed
/// to the right process. Implemented by <c>StepRunnerService</c>.
/// </summary>
public interface IStepSessionSink
{
    void AttachSession(string ticketKey, IAgentSession session);
}
