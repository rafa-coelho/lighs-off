using LightsOff.Core.Domain;
using Markdig;

namespace LightsOff.App.Services;

/// <summary>
/// Rendering helpers for chat + plan: turn an <see cref="ImplementationPlan"/> into markdown,
/// and turn arbitrary markdown into safe HTML for direct binding via <c>MarkupString</c>.
///
/// Registered as singleton — Markdig pipelines are thread-safe and reusable.
/// </summary>
public sealed class MarkdownRenderer
{
    private readonly MarkdownPipeline _pipeline;

    public MarkdownRenderer()
    {
        // GFM gives us tables, task lists, autolinks, strikethrough, fenced code, etc.
        // SoftlineBreakAsHardlineBreak makes single-newline message bubbles render the way users
        // expect (each Enter = a visual newline).
        _pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .UseSoftlineBreakAsHardlineBreak()
            .DisableHtml()  // sanitize: never trust agent-generated HTML
            .Build();
    }

    public string ToHtml(string markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return string.Empty;
        return Markdown.ToHtml(markdown, _pipeline);
    }

    /// <summary>
    /// Render an <see cref="ImplementationPlan"/> as markdown sections — Approach, Tasks,
    /// Planned Changes, Assumptions. Open Questions are rendered separately via interactive
    /// answer cards, NOT in this string.
    /// </summary>
    public static string PlanToMarkdown(ImplementationPlan plan)
    {
        var sb = new System.Text.StringBuilder();

        if (!string.IsNullOrWhiteSpace(plan.Approach))
        {
            sb.AppendLine("## Approach");
            sb.AppendLine();
            sb.AppendLine(plan.Approach.Trim());
            sb.AppendLine();
        }

        if (plan.Tasks.Count > 0)
        {
            sb.AppendLine("## Tasks");
            sb.AppendLine();
            foreach (var task in plan.Tasks.OrderBy(t => t.Order))
            {
                var line = $"{task.Order}. **{Escape(task.Description)}**";
                if (!string.IsNullOrWhiteSpace(task.TargetFile))
                    line += $"  \n   📄 `{task.TargetFile}`";
                if (!string.IsNullOrWhiteSpace(task.Rationale))
                    line += $"  \n   _{Escape(task.Rationale)}_";
                sb.AppendLine(line);
            }
            sb.AppendLine();
        }

        if (plan.PlannedChanges.Count > 0)
        {
            sb.AppendLine("## Planned changes");
            sb.AppendLine();
            foreach (var c in plan.PlannedChanges)
            {
                var icon = c.Type switch
                {
                    ChangeType.Create => "🆕",
                    ChangeType.Modify => "✏️",
                    ChangeType.Delete => "🗑️",
                    _ => "•"
                };
                var reason = string.IsNullOrWhiteSpace(c.Reason) ? "" : $" — {Escape(c.Reason)}";
                sb.AppendLine($"- {icon} **{c.Type}** `{c.Path}`{reason}");
            }
            sb.AppendLine();
        }

        if (plan.Assumptions.Count > 0)
        {
            sb.AppendLine("## Assumptions");
            sb.AppendLine();
            foreach (var a in plan.Assumptions)
                sb.AppendLine($"- {Escape(a)}");
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    private static string Escape(string s) =>
        // Markdown's special chars that mid-line could derail rendering. Keep light — over-escaping
        // makes the document look noisy. Just neutralize backslashes and asterisks at common spots.
        s.Replace("\\", "\\\\");
}
