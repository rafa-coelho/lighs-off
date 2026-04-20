using LightsOff.Core.Abstractions;

namespace LightsOff.Core.Domain;

public enum PipelineStep { Analyze, Plan, Implement, Validate, Review, Submit }
public enum StepStatus { Pending, Running, Passed, Failed, Skipped, Warning, AwaitingApproval }

public class WorkflowState
{
    public Dictionary<PipelineStep, StepStatus> Steps { get; set; } = new()
    {
        [PipelineStep.Analyze] = StepStatus.Pending,
        [PipelineStep.Plan] = StepStatus.Pending,
        [PipelineStep.Implement] = StepStatus.Pending,
        [PipelineStep.Validate] = StepStatus.Pending,
        [PipelineStep.Review] = StepStatus.Pending,
        [PipelineStep.Submit] = StepStatus.Pending,
    };

    public TicketAnalysis? Analysis { get; set; }
    public ImplementationPlan? Plan { get; set; }
    public TestResult? TestResult { get; set; }
    public CodeReview? ReviewResult { get; set; }
    public IReadOnlyList<PullRequestResult>? SubmitResults { get; set; }

    public StepStatus CurrentStep => Steps
        .OrderBy(kv => kv.Key)
        .FirstOrDefault(kv => kv.Value is StepStatus.Running or StepStatus.Pending or StepStatus.AwaitingApproval)
        .Value;

    public bool IsStepUnlocked(PipelineStep step)
    {
        if (step == PipelineStep.Analyze) return true;
        if (step == PipelineStep.Plan)
            return Steps[PipelineStep.Analyze] is StepStatus.Passed or StepStatus.Warning;
        if (step == PipelineStep.Implement)
            return Plan?.IsApproved == true;
        // Validate, Review, Submit unlock linearly after Implement
        var previous = (PipelineStep)((int)step - 1);
        return Steps[previous] is StepStatus.Passed or StepStatus.Warning or StepStatus.Skipped;
    }
}
