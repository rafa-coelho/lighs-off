using LightsOff.Core.Domain;
using LightsOff.Infrastructure.Pipeline;

namespace LightsOff.Tests;

public class TicketQualityCheckerTests
{
    [Fact]
    public void Empty_ticket_flags_no_description_and_no_ac()
    {
        var t = new Ticket { Key = "T-1", Summary = "fix it" };
        var gaps = TicketQualityChecker.Check(t);

        Assert.Contains(gaps, g => g.Rule == "no-description");
        Assert.Contains(gaps, g => g.Rule == "no-acceptance-criteria");
        Assert.True(TicketQualityChecker.HasBlockingConcerns(gaps));
    }

    [Fact]
    public void Well_specified_ticket_has_no_blocking_concerns()
    {
        var t = new Ticket
        {
            Key = "T-1",
            Summary = "Add login throttling",
            Description = "Throttle login attempts to 5 per minute per IP. Return HTTP 429 when exceeded with a Retry-After header. Reset the counter on successful login.",
            AcceptanceCriteria = "- [ ] 6th attempt within 60s returns 429\n- [ ] Successful login clears counter",
            Assignee = "alice",
            StoryPoints = 3
        };
        var gaps = TicketQualityChecker.Check(t);

        Assert.False(TicketQualityChecker.HasBlockingConcerns(gaps));
    }

    [Fact]
    public void Tbd_marker_raises_warning()
    {
        var t = new Ticket
        {
            Key = "T-1",
            Summary = "feature x",
            Description = "Ship this. Threshold: TBD.",
            AcceptanceCriteria = "- [ ] something happens"
        };
        var gaps = TicketQualityChecker.Check(t);
        Assert.Contains(gaps, g => g.Rule == "contains-tbd" && g.Severity == GapSeverity.Warning);
    }

    [Fact]
    public void Open_blocker_is_an_error()
    {
        var t = new Ticket
        {
            Key = "T-1",
            Summary = "summary",
            Description = "A long enough description for the heuristic.",
            AcceptanceCriteria = "- [ ] does the thing",
            LinkedIssues = new List<LinkedIssue>
            {
                new("T-2", "blocker summary", "In Progress", "is blocked by")
            }
        };
        var gaps = TicketQualityChecker.Check(t);
        Assert.Contains(gaps, g => g.Rule == "open-blockers" && g.Severity == GapSeverity.Error);
        Assert.True(TicketQualityChecker.HasBlockingConcerns(gaps));
    }

    [Fact]
    public void Done_blocker_is_ignored()
    {
        var t = new Ticket
        {
            Key = "T-1",
            Summary = "summary",
            Description = "A long enough description for the heuristic.",
            AcceptanceCriteria = "- [ ] does the thing",
            LinkedIssues = new List<LinkedIssue>
            {
                new("T-2", "old blocker", "Done", "is blocked by")
            }
        };
        var gaps = TicketQualityChecker.Check(t);
        Assert.DoesNotContain(gaps, g => g.Rule == "open-blockers");
    }

    [Fact]
    public void Description_with_inline_checklist_satisfies_ac_check()
    {
        var t = new Ticket
        {
            Key = "T-1",
            Summary = "summary",
            Description = "Long enough description for the heuristic.\n- [ ] item one\n- [ ] item two",
            AcceptanceCriteria = null
        };
        var gaps = TicketQualityChecker.Check(t);
        Assert.DoesNotContain(gaps, g => g.Rule == "no-acceptance-criteria");
    }
}
