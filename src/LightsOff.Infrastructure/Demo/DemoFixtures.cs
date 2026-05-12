using LightsOff.Core.Domain;

namespace LightsOff.Infrastructure.Demo;

/// <summary>
/// Hand-authored fixtures for the deterministic demo flow. One board, three tickets, one rich
/// "hero" ticket used to drive the live presentation. Used by every Demo* fake so the surfaces
/// agree on names and IDs.
/// </summary>
internal static class DemoFixtures
{
    public const string BoardName = "Payments Squad";
    public const int BoardId = 42;
    public const string ProjectKey = "PAY";

    public const string DemoRepo = "checkout-api";
    public const string DemoOwner = "demo-org";
    public const string DemoDefaultBranch = "main";

    public const string HeroTicketKey = "PAY-101";

    public static IReadOnlyList<Board> Boards { get; } = new[]
    {
        new Board(BoardId, BoardName, ProjectKey, "scrum"),
    };

    public static IReadOnlyList<Ticket> Tickets { get; } =
    [
        new Ticket
        {
            Key = HeroTicketKey,
            Summary = "Add rate limiting to /api/checkout",
            Status = "To Do",
            Assignee = "Rafael Coelho",
            Type = "Story",
            Url = $"https://demo.atlassian.net/browse/{HeroTicketKey}",
            Description =
                "The checkout endpoint is taking traffic spikes from a single client during marketing pushes. " +
                "We need a request-rate limit per API key so that one noisy tenant can't degrade latency for everyone else.\n\n" +
                "Use a token-bucket algorithm. Default budget: 60 requests/minute. Return HTTP 429 with a Retry-After header " +
                "when the bucket is empty.",
            AcceptanceCriteria =
                "- Requests beyond the configured budget receive HTTP 429.\n" +
                "- Response includes a Retry-After header in seconds.\n" +
                "- Limit is per API key (header X-API-Key); unauthenticated requests share a single bucket.\n" +
                "- Configurable via appsettings (RateLimit:RequestsPerMinute).\n" +
                "- Covered by unit + integration tests.",
            StoryPoints = 5,
            EpicKey = "PAY-90",
            EpicName = "Platform hardening Q2",
            Labels = ["api", "reliability", "hardening"],
            Components = ["checkout-api"],
            Comments =
            [
                new TicketComment("Maya (PM)", "Marketing is launching the campaign on May 20 — would like this in before then.", DateTimeOffset.UtcNow.AddDays(-3)),
                new TicketComment("Daniel (Tech Lead)", "+1 to token bucket. We already have IMemoryCache so no new infra needed.", DateTimeOffset.UtcNow.AddDays(-2)),
            ],
            SubTasks = [],
            LinkedIssues = [new LinkedIssue("PAY-87", "Add structured request logging", "Done", "relates to")],
        },
        new Ticket
        {
            Key = "PAY-102",
            Summary = "Surface refund failures in the ops dashboard",
            Status = "To Do",
            Assignee = "Priya Sharma",
            Type = "Task",
            Url = "https://demo.atlassian.net/browse/PAY-102",
            Description = "Refund failures currently only land in logs. Surface them on the ops dashboard with a retry button.",
            StoryPoints = 3,
            Labels = ["ops"],
        },
        new Ticket
        {
            Key = "PAY-103",
            Summary = "Investigate intermittent 503s from Stripe webhook handler",
            Status = "To Do",
            Assignee = null,
            Type = "Bug",
            Url = "https://demo.atlassian.net/browse/PAY-103",
            Description = "Sporadic 503s reported in #payments-oncall over the past week. Reproduce, triage, fix.",
            Labels = ["bug", "payments"],
        },
    ];

    public static Ticket? FindTicket(string key) =>
        Tickets.FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase));

    // ── Canned analysis / plan content for the hero ticket ─────────────────

    public static TicketAnalysis BuildAnalysisWithQuestion(Ticket ticket) => new()
    {
        TicketKey = ticket.Key,
        AnalyzedAt = DateTimeOffset.UtcNow,
        AgentUsed = "demo",
        AiSummary =
            "Adds a per-API-key rate limit to the checkout endpoint using a token-bucket. " +
            "Existing IMemoryCache will hold the per-key buckets; the middleware sits early in " +
            "the request pipeline so unauthorized traffic also gets throttled.",
        Tasks =
        [
            new TicketTask(1, "Implement token-bucket rate limit middleware", FromTicket: true),
            new TicketTask(2, "Wire middleware into the request pipeline", FromTicket: true),
            new TicketTask(3, "Add appsettings binding for RequestsPerMinute", FromTicket: true),
            new TicketTask(4, "Add unit + integration tests", FromTicket: true),
        ],
        RelevantFiles =
        [
            new RelevantFile("src/Api/Program.cs", "Middleware pipeline registration"),
            new RelevantFile("src/Api/Middleware/", "New middleware will live here"),
            new RelevantFile("src/Api/appsettings.json", "Configuration binding"),
            new RelevantFile("tests/Api.Tests/RateLimitTests.cs", "New test file"),
        ],
        Gaps =
        [
            new Gap(GapSeverity.Info, "ac.clarity", "Acceptance criteria does not specify behavior for unauthenticated traffic — assume a shared bucket."),
        ],
        OpenQuestions =
        [
            new OpenQuestion(
                "Should the rate limit apply per API key or per source IP?",
                ["Per API key (header X-API-Key)", "Per source IP", "Both — whichever budget is exhausted first"]),
        ],
    };

    public static TicketAnalysis BuildAnalysisAfterAnswer(Ticket ticket, string userAnswer)
    {
        var analysis = BuildAnalysisWithQuestion(ticket);
        analysis.OpenQuestions.Clear();
        analysis.AiSummary +=
            $"\n\nClarification from author: \"{userAnswer}\". Plan updated accordingly.";
        return analysis;
    }

    public static ImplementationPlan BuildPlan(Ticket ticket) => new()
    {
        TicketKey = ticket.Key,
        CreatedAt = DateTimeOffset.UtcNow,
        AgentUsed = "demo",
        Status = PlanStatus.AwaitingApproval,
        Approach =
            "Introduce a `RateLimitMiddleware` that maintains per-API-key token buckets in IMemoryCache. " +
            "Register the middleware early in Program.cs — before routing — so 429s are returned before any handler work. " +
            "Bind `RateLimit:RequestsPerMinute` from appsettings; default to 60. Emit a structured log line on every reject " +
            "for the ops dashboard ticket (PAY-102).",
        Tasks =
        [
            new PlannedTask(1, "Add RateLimitOptions record bound to RateLimit section in appsettings.", "src/Api/Configuration/RateLimitOptions.cs", "Strong-typed config + defaults."),
            new PlannedTask(2, "Implement RateLimitMiddleware using IMemoryCache and a token-bucket algorithm.", "src/Api/Middleware/RateLimitMiddleware.cs", "Per-key budget; 429 + Retry-After on exhaustion."),
            new PlannedTask(3, "Register middleware in Program.cs before routing.", "src/Api/Program.cs", "Position matters — must run before any expensive handler."),
            new PlannedTask(4, "Add unit tests for the bucket math and integration tests for the 429 response.", "tests/Api.Tests/RateLimitTests.cs", "Cover happy path, exhausted bucket, and Retry-After header."),
        ],
        PlannedChanges =
        [
            new FileChange("src/Api/Configuration/RateLimitOptions.cs", ChangeType.Create, "New options record."),
            new FileChange("src/Api/Middleware/RateLimitMiddleware.cs", ChangeType.Create, "Token-bucket middleware."),
            new FileChange("src/Api/Program.cs", ChangeType.Modify, "Register middleware + options."),
            new FileChange("src/Api/appsettings.json", ChangeType.Modify, "Add RateLimit:RequestsPerMinute = 60."),
            new FileChange("tests/Api.Tests/RateLimitTests.cs", ChangeType.Create, "Unit + integration tests."),
        ],
        Assumptions =
        [
            "IMemoryCache is already registered in the DI container (verified — Program.cs line 24).",
            "X-API-Key middleware runs after authentication; auth is not in scope for this ticket.",
            "60 requests per minute is the agreed default until the campaign launches; PM can tune later.",
        ],
        OpenQuestions = [],
        Comments = [],
    };
}
