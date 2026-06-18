using LightsOff.Core.Domain;
using LightsOff.Infrastructure.Chat;

namespace LightsOff.Tests;

public class KeywordIntentRouterTests
{
    private static WorkflowState Fresh() => new();

    private static WorkflowState WithApprovedPlan()
    {
        var s = new WorkflowState
        {
            Plan = new ImplementationPlan { TicketKey = "T-1", Status = PlanStatus.Approved }
        };
        s.Steps[PipelineStep.Analyze] = StepStatus.Passed;
        s.Steps[PipelineStep.Plan] = StepStatus.Passed;
        return s;
    }

    private static WorkflowState AfterImplement()
    {
        var s = WithApprovedPlan();
        s.Steps[PipelineStep.Implement] = StepStatus.Passed;
        return s;
    }

    private static WorkflowState WithOpenPr()
    {
        var s = AfterImplement();
        s.Steps[PipelineStep.Validate] = StepStatus.Passed;
        s.SubmitResults = new[]
        {
            new PullRequestResult("repo", "feature/T-1", "https://github.com/x/repo/pull/1", null)
        };
        return s;
    }

    [Fact]
    public void Empty_message_is_chat()
    {
        var c = KeywordIntentRouter.Classify("", Fresh(), hasOpenQuestions: false);
        Assert.Equal(ChatIntent.Chat, c.Intent);
    }

    [Fact]
    public void Plain_question_is_chat()
    {
        var c = KeywordIntentRouter.Classify("what does this ticket actually require?", Fresh(), false);
        Assert.Equal(ChatIntent.Chat, c.Intent);
    }

    [Theory]
    [InlineData("implementa isso agora")]
    [InlineData("ok, implement it")]
    [InlineData("manda ver na implementação")]
    public void Implement_keywords_with_approved_plan_are_actionable(string msg)
    {
        var c = KeywordIntentRouter.Classify(msg, WithApprovedPlan(), false);
        Assert.Equal(ChatIntent.Implement, c.Intent);
        Assert.True(c.Actionable);
    }

    [Fact]
    public void Implement_without_approved_plan_is_understood_but_not_actionable()
    {
        var c = KeywordIntentRouter.Classify("implement it", Fresh(), false);
        Assert.Equal(ChatIntent.Implement, c.Intent);
        Assert.False(c.Actionable);
        Assert.False(string.IsNullOrWhiteSpace(c.Rationale));
    }

    [Theory]
    [InlineData("rode os testes")]
    [InlineData("run the tests now")]
    [InlineData("valida isso")]
    public void Test_keywords_after_implement_are_actionable(string msg)
    {
        var c = KeywordIntentRouter.Classify(msg, AfterImplement(), false);
        Assert.Equal(ChatIntent.RunTests, c.Intent);
        Assert.True(c.Actionable);
    }

    [Theory]
    [InlineData("abre o PR")]
    [InlineData("open the pr please")]
    [InlineData("manda o pr")]
    public void Submit_keywords_after_implement_are_actionable(string msg)
    {
        var c = KeywordIntentRouter.Classify(msg, AfterImplement(), false);
        Assert.Equal(ChatIntent.Submit, c.Intent);
        Assert.True(c.Actionable);
    }

    [Fact]
    public void Approve_with_draft_plan_is_actionable()
    {
        var s = new WorkflowState { Plan = new ImplementationPlan { TicketKey = "T-1", Status = PlanStatus.Draft } };
        s.Steps[PipelineStep.Analyze] = StepStatus.Passed;
        var c = KeywordIntentRouter.Classify("aprova o plano", s, false);
        Assert.Equal(ChatIntent.ApprovePlan, c.Intent);
        Assert.True(c.Actionable);
    }

    [Fact]
    public void Approve_when_already_approved_is_not_actionable()
    {
        var c = KeywordIntentRouter.Classify("approve", WithApprovedPlan(), false);
        Assert.Equal(ChatIntent.ApprovePlan, c.Intent);
        Assert.False(c.Actionable);
    }

    [Fact]
    public void Free_text_with_open_questions_is_an_answer()
    {
        var c = KeywordIntentRouter.Classify("use postgres, not sqlite", Fresh(), hasOpenQuestions: true);
        Assert.Equal(ChatIntent.AnswerQuestion, c.Intent);
        Assert.True(c.Actionable);
    }

    [Fact]
    public void Cancel_is_always_actionable()
    {
        var c = KeywordIntentRouter.Classify("cancela isso", AfterImplement(), false);
        Assert.Equal(ChatIntent.Cancel, c.Intent);
        Assert.True(c.Actionable);
    }

    [Fact]
    public void Address_pr_findings_needs_an_open_pr()
    {
        var without = KeywordIntentRouter.Classify("resolve os comentários da PR", AfterImplement(), false);
        Assert.Equal(ChatIntent.AddressPrFindings, without.Intent);
        Assert.False(without.Actionable);

        var with = KeywordIntentRouter.Classify("resolve os comentários da PR", WithOpenPr(), false);
        Assert.Equal(ChatIntent.AddressPrFindings, with.Intent);
        Assert.True(with.Actionable);
    }

    [Fact]
    public void Review_pr_needs_an_open_pr()
    {
        var c = KeywordIntentRouter.Classify("olha os findings da pr", WithOpenPr(), false);
        Assert.Equal(ChatIntent.ReviewPr, c.Intent);
        Assert.True(c.Actionable);
    }

    [Theory]
    [InlineData("faz tudo automático")]
    [InlineData("roda tudo de ponta a ponta")]
    public void Autonomous_keywords_with_approved_plan_are_actionable(string msg)
    {
        var c = KeywordIntentRouter.Classify(msg, WithApprovedPlan(), false);
        Assert.Equal(ChatIntent.RunAutonomous, c.Intent);
        Assert.True(c.Actionable);
    }

    [Fact]
    public void Diacritics_are_ignored()
    {
        // "análise" must classify the same as "analise".
        var c = KeywordIntentRouter.Classify("pode analisar de novo?", Fresh(), false);
        Assert.Equal(ChatIntent.Analyze, c.Intent);
        Assert.True(c.Actionable);
    }

    [Fact]
    public void Payload_preserves_the_original_message()
    {
        const string msg = "muda o plano para usar Prisma";
        var c = KeywordIntentRouter.Classify(msg, WithApprovedPlan(), false);
        Assert.Equal(ChatIntent.ReworkPlan, c.Intent);
        Assert.Equal(msg, c.Payload);
    }
}
