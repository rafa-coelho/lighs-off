using LightsOff.Core.Abstractions;
using LightsOff.Core.Domain;

namespace LightsOff.Infrastructure.Demo;

/// <summary>
/// Canned <see cref="ICodeAgent"/> for the demo flow. Returns deterministic analysis, plan,
/// review, and coverage results with small delays so the UI shows progress without being slow.
/// </summary>
public sealed class DemoCodeAgent : ICodeAgent
{
    public const string AgentIdConst = "demo";
    private static readonly TimeSpan ShortDelay = TimeSpan.FromMilliseconds(220);
    private static readonly TimeSpan MediumDelay = TimeSpan.FromMilliseconds(550);

    public string Id => AgentIdConst;
    public string DisplayName => "Demo Agent";
    public bool RequiresApiKey => false;
    public bool IsAvailable => true;

    public async Task<RepoContext> AnalyzeRepositoryAsync(string repoPath, RepoContext? existingContext = null, CancellationToken ct = default)
    {
        await Task.Delay(ShortDelay, ct).ConfigureAwait(false);
        return new RepoContext
        {
            RepoName = string.IsNullOrEmpty(repoPath) ? DemoFixtures.DemoRepo : Path.GetFileName(repoPath),
            AnalyzedAt = DateTimeOffset.UtcNow,
            AgentUsed = AgentIdConst,
            TechStack = ".NET 8, ASP.NET Core, EF Core, xUnit",
            EntryPoints = new() { "src/Api/Program.cs" },
            ConfigFiles = new() { "src/Api/appsettings.json", "Directory.Build.props" },
            ApiSurface = new()
            {
                new ApiEndpoint("POST", "/api/checkout", "src/Api/Endpoints/CheckoutEndpoint.cs", 24),
                new ApiEndpoint("GET",  "/api/checkout/{id}", "src/Api/Endpoints/CheckoutEndpoint.cs", 41),
            },
            ArchitectureOverview =
                "Minimal-API checkout service with a thin handler layer over an in-memory cart store. " +
                "Auth runs early, no rate limiting exists today — this ticket fills that gap.",
            BusinessDomains = new() { "Payments", "Checkout" },
            CodeQualityNotes = new() { "Test coverage solid (84% on the API surface)." },
        };
    }

    public async Task<TicketAnalysis> AnalyzeTicketAsync(Ticket ticket, IReadOnlyList<RepoContext> repoContexts, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        progress?.Report("Reading ticket description and acceptance criteria…");
        await Task.Delay(ShortDelay, ct).ConfigureAwait(false);
        progress?.Report("Cross-referencing with checkout-api surface…");
        await Task.Delay(MediumDelay, ct).ConfigureAwait(false);
        progress?.Report("Drafting task breakdown…");
        await Task.Delay(ShortDelay, ct).ConfigureAwait(false);

        var analysis = string.Equals(ticket.Key, DemoFixtures.HeroTicketKey, StringComparison.OrdinalIgnoreCase)
            ? DemoFixtures.BuildAnalysisWithQuestion(ticket)
            : new TicketAnalysis
            {
                TicketKey = ticket.Key,
                AnalyzedAt = DateTimeOffset.UtcNow,
                AgentUsed = AgentIdConst,
                AiSummary = $"Skeleton analysis for {ticket.Key}. No blockers detected.",
                Tasks = new() { new TicketTask(1, "Implement the ticket as described.", true) },
            };
        return analysis;
    }

    public async Task<CodeReview> ReviewChangesAsync(string diff, RepoContext context, CancellationToken ct = default)
    {
        await Task.Delay(ShortDelay, ct).ConfigureAwait(false);
        return new CodeReview
        {
            ReviewedAt = DateTimeOffset.UtcNow,
            AgentUsed = AgentIdConst,
            AiSummary = "Token-bucket implementation is correct and well-tested. No concerns.",
        };
    }

    public async Task<RepoDependencyProfile> DetectDependenciesAsync(string repoPath, CancellationToken ct = default)
    {
        await Task.Delay(ShortDelay, ct).ConfigureAwait(false);
        return new RepoDependencyProfile
        {
            RepositoryName = string.IsNullOrEmpty(repoPath) ? DemoFixtures.DemoRepo : Path.GetFileName(repoPath),
            DetectedAt = DateTimeOffset.UtcNow,
            AgentUsed = AgentIdConst,
            Runtimes = new() { new RuntimeRequirement("dotnet", "8.0", null) },
            PackageInstallCommands = new() { "dotnet restore" },
            CustomCommands = new(),
        };
    }

    public async Task<ImplementationPlan> GeneratePlanAsync(Ticket ticket, TicketAnalysis analysis, IReadOnlyList<RepoContext> repoContexts, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        progress?.Report("Drafting approach…");
        await Task.Delay(ShortDelay, ct).ConfigureAwait(false);
        progress?.Report("Sequencing tasks…");
        await Task.Delay(MediumDelay, ct).ConfigureAwait(false);
        progress?.Report("Listing planned changes…");
        await Task.Delay(ShortDelay, ct).ConfigureAwait(false);
        return DemoFixtures.BuildPlan(ticket);
    }

    public Task<IAgentSession> StartSessionAsync(AgentSessionSpec spec, CancellationToken ct = default) =>
        Task.FromResult<IAgentSession>(new DemoAgentSession(spec));

    public async Task<CoverageReport> MeasureNewCodeCoverageAsync(string repoPath, string baseBranch, CancellationToken ct = default)
    {
        await Task.Delay(ShortDelay, ct).ConfigureAwait(false);
        return new CoverageReport
        {
            RepositoryName = string.IsNullOrEmpty(repoPath) ? DemoFixtures.DemoRepo : Path.GetFileName(repoPath),
            AgentUsed = AgentIdConst,
            MeasuredAt = DateTimeOffset.UtcNow,
            OverallLinePercent = 87.6,
            NewCodeLinePercent = 92.3,
            NewCodeLinesTotal = 78,
            NewCodeLinesCovered = 72,
            Files = new(),
            Error = null,
        };
    }

    public async Task<ChatReplyEvent> ReplyToPlanChatAsync(ImplementationPlan plan, string userMessage, CancellationToken ct = default)
    {
        await Task.Delay(ShortDelay, ct).ConfigureAwait(false);
        return new ChatReplyEvent(
            DateTimeOffset.UtcNow,
            "Got it. I'll fold that into the plan on the next rework.",
            IsFinal: true);
    }

    public async Task<ImplementationPlan> ReworkPlanAsync(Ticket ticket, ImplementationPlan previousPlan, string userFeedback, IReadOnlyList<RepoContext> repoContexts, CancellationToken ct = default)
    {
        await Task.Delay(MediumDelay, ct).ConfigureAwait(false);
        var plan = DemoFixtures.BuildPlan(ticket);
        plan.Status = PlanStatus.Draft;
        plan.Approach += $"\n\nUpdated for feedback: \"{userFeedback}\".";
        return plan;
    }

    public async Task<AcReviewReport> ReviewAgainstAcAsync(Ticket ticket, string diff, RepoContext context, CancellationToken ct = default)
    {
        await Task.Delay(ShortDelay, ct).ConfigureAwait(false);
        return new AcReviewReport
        {
            TicketKey = ticket.Key,
            AgentUsed = AgentIdConst,
            ReviewedAt = DateTimeOffset.UtcNow,
            SatisfiesAc = true,
            Concerns = new(),
            Summary = "Diff introduces a per-API-key rate limit returning 429 + Retry-After, configurable via appsettings, with unit + integration tests — matches every acceptance bullet.",
        };
    }

    public async Task<TicketAnalysis> RefineAnalysisAsync(Ticket ticket, TicketAnalysis previousAnalysis, IReadOnlyList<ChatMessage> conversation, IReadOnlyList<RepoContext> repoContexts, CancellationToken ct = default)
    {
        await Task.Delay(MediumDelay, ct).ConfigureAwait(false);
        // Use the user's last message as the "answer" the demo will echo into the refined summary.
        var lastUser = conversation.LastOrDefault(m => string.Equals(m.Role, ChatMessage.RoleUser, StringComparison.OrdinalIgnoreCase));
        var answer = lastUser?.Content ?? "per API key";
        var refined = DemoFixtures.BuildAnalysisAfterAnswer(ticket, answer);
        refined.Conversation.AddRange(conversation);
        return refined;
    }
}
