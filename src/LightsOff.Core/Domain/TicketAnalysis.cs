namespace LightsOff.Core.Domain;

public class TicketAnalysis
{
    public string TicketKey { get; set; } = string.Empty;
    public DateTimeOffset AnalyzedAt { get; set; } = DateTimeOffset.UtcNow;
    public string AgentUsed { get; set; } = "local";

    public List<Gap> Gaps { get; set; } = new();
    public List<TicketTask> Tasks { get; set; } = new();
    public List<RelevantFile> RelevantFiles { get; set; } = new();
    public List<OpenQuestion> OpenQuestions { get; set; } = new();
    public string? AiSummary { get; set; }

    /// <summary>
    /// Chronological back-and-forth between the user and the agent during the Analyze step —
    /// user clarifications, AI follow-up summaries, etc. Persisted alongside the analysis so
    /// re-runs see the full conversation history.
    /// </summary>
    public List<ChatMessage> Conversation { get; set; } = new();

    public bool HasErrors => Gaps.Any(g => g.Severity == GapSeverity.Error);
    public bool HasWarnings => Gaps.Any(g => g.Severity == GapSeverity.Warning);
}

public enum GapSeverity { Error, Warning, Info }
public record Gap(GapSeverity Severity, string Rule, string Detail);
public record TicketTask(int Order, string Description, bool FromTicket);
public record RelevantFile(string Path, string Reason);

/// <summary>One turn in a conversation between the user and the agent.</summary>
public record ChatMessage(string Role, string Content, DateTimeOffset At, ChatAttachment? Attachment = null)
{
    public const string RoleUser = "user";
    public const string RoleAgent = "agent";
    public const string RoleSystem = "system";
}

/// <summary>
/// Inline interactive content embedded inside an agent chat message — a question card with
/// suggestions, a plan summary card that opens a side panel, etc. <see cref="Kind"/> is the
/// discriminator the UI dispatches on; <see cref="PayloadJson"/> carries the kind-specific data.
/// </summary>
public record ChatAttachment(string Kind, string Title, string? Subtitle, string? PayloadJson)
{
    public const string KindQuestions = "questions";
    public const string KindPlan = "plan";
}

/// <summary>
/// Payload shape for <c>ChatAttachment.KindQuestions</c> attachments. A single chat-message
/// attachment carries a whole batch of questions; the user advances through them inline using
/// the prev/next arrows in the card header.
/// </summary>
public sealed class QuestionsPayload
{
    public string BatchId { get; set; } = Guid.NewGuid().ToString("N");
    public List<OpenQuestion> Questions { get; set; } = new();
    public Dictionary<int, string> Answers { get; set; } = new();  // index -> answer (or "" for skip)
    public bool Closed { get; set; }
}
