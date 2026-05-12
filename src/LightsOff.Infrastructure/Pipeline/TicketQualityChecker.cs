using System.Text.RegularExpressions;
using LightsOff.Core.Domain;

namespace LightsOff.Infrastructure.Pipeline;

/// <summary>
/// Pre-flight heuristic check that flags tickets that are too vague or under-specified to
/// safely hand to the AI pipeline. Same rules as <c>LocalCodeAgent.AnalyzeTicketAsync</c> but
/// extracted so the autonomous chain can call it before any AI work happens — no claude
/// invocation, no network, no cost.
///
/// Output is a <see cref="IReadOnlyList{Gap}"/>; severity ranges from Info to Error. The UI
/// shows a banner when any Error is present but never blocks — the user can always proceed.
/// </summary>
public static partial class TicketQualityChecker
{
    public static IReadOnlyList<Gap> Check(Ticket ticket)
    {
        var gaps = new List<Gap>();
        var description = ticket.Description ?? string.Empty;
        var ac = ticket.AcceptanceCriteria ?? string.Empty;

        if (string.IsNullOrWhiteSpace(description) || description.Length < 20)
            gaps.Add(new Gap(GapSeverity.Error, "no-description", "Ticket has no meaningful description."));

        if (string.IsNullOrWhiteSpace(ac) && !ContainsChecklist(description))
            gaps.Add(new Gap(GapSeverity.Error, "no-acceptance-criteria", "No acceptance criteria or checklist found."));

        var combined = description + " " + ac;
        foreach (var match in TbdPattern().Matches(combined).Cast<Match>())
            gaps.Add(new Gap(GapSeverity.Warning, "contains-tbd", $"Contains \"{match.Value}\" — needs a decision."));

        foreach (var match in VaguePattern().Matches(combined).Cast<Match>())
            gaps.Add(new Gap(GapSeverity.Warning, "vague-language", $"Vague: \"{match.Value}\" — add measurable criteria."));

        if (string.IsNullOrWhiteSpace(ticket.Assignee))
            gaps.Add(new Gap(GapSeverity.Info, "no-assignee", "Ticket is unassigned."));

        if (ticket.StoryPoints is null or 0)
            gaps.Add(new Gap(GapSeverity.Info, "no-estimate", "No story points estimated."));

        if (ticket.LinkedIssues is not null)
        {
            foreach (var linked in ticket.LinkedIssues.Where(l =>
                l.LinkType.Contains("block", StringComparison.OrdinalIgnoreCase) &&
                !l.Status.Contains("done", StringComparison.OrdinalIgnoreCase) &&
                !l.Status.Contains("closed", StringComparison.OrdinalIgnoreCase)))
            {
                gaps.Add(new Gap(GapSeverity.Error, "open-blockers",
                    $"Blocked by {linked.Key} ({linked.Status})."));
            }
        }

        return gaps;
    }

    /// <summary>True when the gaps include at least one Error — UI surfaces a banner.</summary>
    public static bool HasBlockingConcerns(IReadOnlyList<Gap> gaps) =>
        gaps.Any(g => g.Severity == GapSeverity.Error);

    private static bool ContainsChecklist(string text) =>
        text.Contains("- [ ]", StringComparison.Ordinal) ||
        text.Contains("- [x]", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("* [ ]", StringComparison.Ordinal) ||
        text.Contains("[ ]", StringComparison.Ordinal) ||
        text.Contains("[x]", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"\b(?:TBD|TODO|to be (?:decided|defined|determined))\b", RegexOptions.IgnoreCase)]
    private static partial Regex TbdPattern();

    [GeneratedRegex(@"\b(?:should work|properly|as expected|correctly|ASAP|as needed)\b", RegexOptions.IgnoreCase)]
    private static partial Regex VaguePattern();
}
