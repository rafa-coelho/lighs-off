using System.Diagnostics;
using System.Text;
using LightsOff.Core.Abstractions;
using LightsOff.Core.Domain;
using Microsoft.Extensions.Logging;

namespace LightsOff.Infrastructure.Agents;

/// <summary>
/// Uses the host's local <c>copilot</c> CLI (the standalone GitHub Copilot CLI, npm package
/// <c>@github/copilot</c>) as the agent backend for stateless calls — Analyze, Plan, Review,
/// Rework, AC review.
///
/// Phase 1 scope: <see cref="StartSessionAsync"/> throws <see cref="NotSupportedException"/>.
/// Copilot's CLI doesn't support bidirectional stdin streaming the way Claude Code does — it's
/// session-resume-based (<c>--resume &lt;name&gt;</c>) which requires re-spawning per turn. That's
/// doable but more work than this iteration. For the autonomous chain's Implement step, switch
/// to <c>claude-cli</c>; Copilot can drive everything else.
///
/// Auth: user runs <c>copilot login</c> on the host once. Availability probe checks for the
/// binary on PATH plus <c>~/.copilot/</c> existence.
/// </summary>
public sealed class CopilotCliAgent : ICodeAgent
{
    public const string AgentId = "copilot-cli";

    private readonly LocalCodeAgent _fallback;
    private readonly ILogger<CopilotCliAgent> _logger;
    private readonly Lazy<string?> _copilotPath;

    public CopilotCliAgent(LocalCodeAgent fallback, ILogger<CopilotCliAgent> logger)
    {
        _fallback = fallback;
        _logger = logger;
        _copilotPath = new Lazy<string?>(ResolveCopilotPath);
    }

    public string Id => AgentId;
    public string DisplayName => "GitHub Copilot CLI (host)";
    public bool RequiresApiKey => false;

    public bool IsAvailable
    {
        get
        {
            if (_copilotPath.Value is null) return false;
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home)) return false;
            return Directory.Exists(Path.Combine(home, ".copilot"));
        }
    }

    private CopilotCliOneShot OneShot(TimeSpan? timeout = null) => new(_copilotPath.Value!, _logger, timeout);

    // Plan generation/rework prompts include full repo contexts and richer schemas, so they need
    // more headroom than the 3-minute default used elsewhere.
    private static readonly TimeSpan PlanTimeout = TimeSpan.FromMinutes(10);

    // ── Streaming session: deferred to Phase 2 ────────────────────────────

    public Task<IAgentSession> StartSessionAsync(AgentSessionSpec spec, CancellationToken ct = default)
    {
        EnsureAvailable();
        if (spec.AllowedRepoPaths.Count == 0)
            throw new InvalidOperationException("AgentSessionSpec must include at least one allowed repo path.");

        var cwd = spec.AllowedRepoPaths[0];
        if (!Directory.Exists(cwd))
            throw new DirectoryNotFoundException($"Session CWD not found on host: {cwd}");

        // Copilot doesn't accept stdin streaming — its prompt has to be passed via -p at spawn
        // time. The orchestrator's pattern (StartSession → SendAsync(initial-instruction)) needs
        // the instruction to land in the prompt, so we defer the spawn until SendAsync arrives.
        var basePrompt = BuildSessionBasePrompt(spec);
        IAgentSession session = new CopilotLazySession(_copilotPath.Value!, spec, basePrompt, _logger);
        return Task.FromResult(session);
    }

    private static string BuildSessionBasePrompt(AgentSessionSpec spec)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"You are working on ticket {spec.TicketKey} inside the lights-off development environment.");
        sb.AppendLine($"Mode: {spec.Mode}.");
        sb.AppendLine();
        sb.AppendLine($"Ticket summary: {spec.Ticket.Summary}");
        if (!string.IsNullOrWhiteSpace(spec.Ticket.Description))
        {
            var desc = spec.Ticket.Description!;
            sb.AppendLine("Description:");
            sb.AppendLine(desc.Length > 2000 ? desc[..2000] + "…" : desc);
        }
        if (!string.IsNullOrWhiteSpace(spec.Ticket.AcceptanceCriteria))
        {
            sb.AppendLine("Acceptance criteria:");
            sb.AppendLine(spec.Ticket.AcceptanceCriteria);
        }
        if (spec.Plan is not null && spec.Plan.Tasks.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Approved implementation plan — execute these tasks in order:");
            foreach (var task in spec.Plan.Tasks)
            {
                sb.AppendLine($"  {task.Order}. {task.Description}");
                if (!string.IsNullOrEmpty(task.Rationale)) sb.AppendLine($"     ({task.Rationale})");
            }
        }
        sb.AppendLine();
        sb.AppendLine("IMPORTANT: Do NOT run build, test, or package-install commands.");
        sb.AppendLine("The lights-off pipeline runs those inside the dev container and will report results back separately.");
        sb.AppendLine("Read-only inspection commands (ls, cat, git log, git diff, rg, grep) are fine.");
        // No closing instruction here — the caller's SendAsync delivers the actual task and
        // CopilotLazySession appends it to this base before spawning the process.
        return sb.ToString();
    }

    public Task<ChatReplyEvent> ReplyToPlanChatAsync(ImplementationPlan plan, string userMessage, CancellationToken ct = default)
    {
        EnsureAvailable();
        throw new NotSupportedException(
            "Copilot CLI plan-chat replies are not yet implemented. Use the chat in the Plan step UI " +
            "(which calls ReworkPlanAsync) for iterative refinement.");
    }

    // ── Repo analysis: local mechanical scan + AI enrichment ──────────────

    public async Task<RepoContext> AnalyzeRepositoryAsync(string repoPath, RepoContext? existingContext = null, CancellationToken ct = default)
    {
        EnsureAvailable();
        var ctx = await _fallback.AnalyzeRepositoryAsync(repoPath, existingContext, ct).ConfigureAwait(false);
        ctx.AgentUsed = Id;

        var prompt = BuildRepoAnalysisPrompt(ctx);
        try
        {
            var ai = await OneShot().RunForJsonAsync<RepoAiFields>(prompt, cwd: repoPath, ct: ct).ConfigureAwait(false);
            ctx.ArchitectureOverview = ai.ArchitectureOverview;
            ctx.Features = ai.Features?.Select(f => new RepoFeature(f.Name, f.Description, f.RelatedFiles)).ToList();
            ctx.DataFlowDescription = ai.DataFlowDescription;
            ctx.BusinessDomains = ai.BusinessDomains;
            ctx.CodeQualityNotes = ai.CodeQualityNotes;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "copilot-cli repo enrichment failed for {Repo}; returning local-only context", ctx.RepoName);
        }

        RepoContextSerializer.Save(repoPath, ctx);
        return ctx;
    }

    // ── Ticket analysis ────────────────────────────────────────────────────

    public async Task<TicketAnalysis> AnalyzeTicketAsync(Ticket ticket, IReadOnlyList<RepoContext> repoContexts, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        EnsureAvailable();
        var prompt = BuildTicketAnalysisPrompt(ticket, repoContexts);
        var ai = await OneShot().RunForJsonAsync<TicketAnalysisDto>(prompt, progress, ct: ct).ConfigureAwait(false);
        return MapAnalysisDto(ticket, ai);
    }

    public async Task<TicketAnalysis> RefineAnalysisAsync(Ticket ticket, TicketAnalysis previousAnalysis, IReadOnlyList<ChatMessage> conversation, IReadOnlyList<RepoContext> repoContexts, CancellationToken ct = default)
    {
        EnsureAvailable();
        var prompt = BuildRefineAnalysisPrompt(ticket, previousAnalysis, conversation, repoContexts);
        var ai = await OneShot().RunForJsonAsync<TicketAnalysisDto>(prompt, ct: ct).ConfigureAwait(false);
        var refined = MapAnalysisDto(ticket, ai);
        refined.Conversation = conversation.ToList();
        return refined;
    }

    private TicketAnalysis MapAnalysisDto(Ticket ticket, TicketAnalysisDto ai) => new()
    {
        TicketKey = ticket.Key,
        AgentUsed = Id,
        Gaps = (ai.Gaps ?? new()).Select(g => new Gap(
            ParseSeverity(g.Severity),
            g.Type ?? "ai-gap",
            g.Detail ?? "")).ToList(),
        Tasks = (ai.Tasks ?? new()).Select((t, i) => new TicketTask(
            t.Order > 0 ? t.Order : i + 1,
            t.Description ?? "",
            t.FromTicket)).ToList(),
        RelevantFiles = (ai.RelevantFiles ?? new()).Select(f => new RelevantFile(
            f.Path ?? "?",
            f.Reason ?? "")).ToList(),
        OpenQuestions = MapOpenQuestions(ai.OpenQuestions)
    };

    // ── Plan generation + rework ─────────────────────────────────────────

    public async Task<ImplementationPlan> GeneratePlanAsync(Ticket ticket, TicketAnalysis analysis, IReadOnlyList<RepoContext> repoContexts, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        EnsureAvailable();
        var prompt = BuildPlanPrompt(ticket, analysis, repoContexts);
        var ai = await OneShot(PlanTimeout).RunForJsonAsync<PlanDto>(prompt, progress, ct: ct).ConfigureAwait(false);
        return MapPlanDto(ticket, ai, PlanStatus.Draft, comments: null);
    }

    public async Task<ImplementationPlan> ReworkPlanAsync(Ticket ticket, ImplementationPlan previousPlan, string userFeedback, IReadOnlyList<RepoContext> repoContexts, CancellationToken ct = default)
    {
        EnsureAvailable();
        var prompt = BuildReworkPrompt(ticket, previousPlan, userFeedback, repoContexts);
        var ai = await OneShot(PlanTimeout).RunForJsonAsync<PlanDto>(prompt, ct: ct).ConfigureAwait(false);
        return MapPlanDto(ticket, ai, PlanStatus.Draft, comments: previousPlan.Comments.ToList());
    }

    private ImplementationPlan MapPlanDto(Ticket ticket, PlanDto ai, PlanStatus status, List<PlanComment>? comments) => new()
    {
        TicketKey = ticket.Key,
        AgentUsed = Id,
        Status = status,
        Approach = ai.Approach ?? "",
        Tasks = (ai.Tasks ?? new()).Select((t, i) => new PlannedTask(
            t.Order > 0 ? t.Order : i + 1,
            t.Description ?? "",
            t.TargetFile,
            t.Rationale)).ToList(),
        PlannedChanges = (ai.PlannedChanges ?? new()).Select(c => new FileChange(
            c.Path ?? "?",
            ParseChangeType(c.Type),
            c.Reason)).ToList(),
        Assumptions = ai.Assumptions ?? new(),
        OpenQuestions = MapOpenQuestions(ai.OpenQuestions),
        Comments = comments ?? new()
    };

    // ── Code review + AC review ──────────────────────────────────────────

    public async Task<CodeReview> ReviewChangesAsync(string diff, RepoContext context, CancellationToken ct = default)
    {
        EnsureAvailable();
        if (string.IsNullOrWhiteSpace(diff))
            return new CodeReview { AgentUsed = Id };

        var prompt = BuildReviewPrompt(diff, context);
        var ai = await OneShot().RunForJsonAsync<ReviewDto>(prompt, ct: ct).ConfigureAwait(false);
        return new CodeReview
        {
            AgentUsed = Id,
            SecurityFindings = (ai.SecurityFindings ?? new()).Select(f => new SecurityFinding(
                f.Severity ?? "medium",
                f.Type ?? "ai-finding",
                f.File ?? "?",
                f.Line ?? 0,
                f.Message ?? "")).ToList(),
            QualityFindings = (ai.QualityFindings ?? new()).Select(f => new QualityFinding(
                f.Type ?? "ai-finding",
                f.File ?? "?",
                f.Message ?? "")).ToList()
        };
    }

    public async Task<AcReviewReport> ReviewAgainstAcAsync(Ticket ticket, string diff, RepoContext context, CancellationToken ct = default)
    {
        EnsureAvailable();
        if (string.IsNullOrWhiteSpace(diff))
        {
            return new AcReviewReport
            {
                TicketKey = ticket.Key,
                AgentUsed = Id,
                SatisfiesAc = false,
                Concerns = new List<string> { "Diff is empty — no code changes to assess against the AC." },
                Summary = "No diff to review."
            };
        }

        var prompt = BuildAcReviewPrompt(ticket, diff, context);
        var ai = await OneShot().RunForJsonAsync<AcReviewDto>(prompt, ct: ct).ConfigureAwait(false);
        return new AcReviewReport
        {
            TicketKey = ticket.Key,
            AgentUsed = Id,
            SatisfiesAc = ai.SatisfiesAc,
            Concerns = ai.Concerns ?? new List<string>(),
            Summary = ai.Summary ?? string.Empty
        };
    }

    // ── Mechanical: delegate to local ─────────────────────────────────────

    public async Task<RepoDependencyProfile> DetectDependenciesAsync(string repoPath, CancellationToken ct = default)
    {
        EnsureAvailable();
        var profile = await _fallback.DetectDependenciesAsync(repoPath, ct).ConfigureAwait(false);
        profile.AgentUsed = Id;
        return profile;
    }

    public async Task<CoverageReport> MeasureNewCodeCoverageAsync(string repoPath, string baseBranch, CancellationToken ct = default)
    {
        EnsureAvailable();
        var report = await _fallback.MeasureNewCodeCoverageAsync(repoPath, baseBranch, ct).ConfigureAwait(false);
        report.AgentUsed = Id;
        return report;
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private void EnsureAvailable()
    {
        if (!IsAvailable)
        {
            var hint = _copilotPath.Value is null
                ? "`copilot` not found on PATH — install with `npm install -g @github/copilot --registry=https://registry.npmjs.org/`."
                : "`~/.copilot/` not found — run `copilot login` once on your host.";
            throw new InvalidOperationException(
                $"GitHub Copilot CLI agent is not available. {hint}");
        }
    }

    private static string? ResolveCopilotPath()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var sep = OperatingSystem.IsWindows() ? ';' : ':';
        foreach (var dir in path.Split(sep, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, OperatingSystem.IsWindows() ? "copilot.exe" : "copilot");
            if (File.Exists(candidate)) return candidate;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        // Common nvm + npm-global paths.
        string[] fallbacks =
        {
            Path.Combine(home, ".nvm", "versions", "node"),
            Path.Combine(home, ".npm-global", "bin", "copilot"),
            "/opt/homebrew/bin/copilot",
            "/usr/local/bin/copilot"
        };
        foreach (var f in fallbacks)
        {
            if (File.Exists(f)) return f;
            // For the nvm root, walk one level deep — pick the most recent node version's bin/copilot.
            if (Directory.Exists(f))
            {
                try
                {
                    var nodeDir = Directory.EnumerateDirectories(f)
                        .OrderByDescending(d => d, StringComparer.Ordinal)
                        .FirstOrDefault(d => File.Exists(Path.Combine(d, "bin", "copilot")));
                    if (nodeDir is not null) return Path.Combine(nodeDir, "bin", "copilot");
                }
                catch { }
            }
        }
        return null;
    }

    private static List<OpenQuestion> MapOpenQuestions(List<OpenQuestionDto>? dtos)
    {
        if (dtos is null) return new();
        return dtos
            .Where(q => !string.IsNullOrWhiteSpace(q.Text))
            .Select(q => new OpenQuestion(q.Text!, (IReadOnlyList<string>)(q.Suggestions ?? new())))
            .ToList();
    }

    private static GapSeverity ParseSeverity(string? s) =>
        s?.Trim().ToLowerInvariant() switch
        {
            "error" => GapSeverity.Error,
            "warning" => GapSeverity.Warning,
            "info" => GapSeverity.Info,
            _ => GapSeverity.Info
        };

    private static ChangeType ParseChangeType(string? s) =>
        s?.Trim().ToLowerInvariant() switch
        {
            "create" => ChangeType.Create,
            "modify" => ChangeType.Modify,
            "delete" => ChangeType.Delete,
            _ => ChangeType.Modify
        };

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    // ── Prompts (mirrors ClaudeCliAgent's prompts; kept self-contained for now) ──

    private const string PlanQualityRules = """
    QUALITY RULES (these are the difference between a plan and a useless checklist):
    - Tasks must be ENGINEERING-LEVEL, not user-story-level. Each task names a concrete file, class, function, migration, endpoint, DI registration, or config change.
    - DO NOT mirror acceptance-criteria checkboxes 1:1 as tasks. The AC describes desired BEHAVIOR. Your plan describes the CODE CHANGES that produce it. These rarely line up one-to-one.
    - Every task must answer: "what file or symbol does this touch, and what is the change in code terms?" If you cannot, the task is too vague — split or sharpen it.
    - Include cross-cutting infrastructure tasks the AC won't mention but the implementation needs: DI wiring, schema/migration changes, error handling at boundaries, type-system updates, config flags, feature toggles.
    - Order tasks by execution dependency, not by AC order. Foundations (types, schemas) before things that use them.
    - 4–12 tasks total. If you have more, you're being too granular; if fewer, too vague.
    - plannedChanges must list exact existing paths for Modify and concrete new paths for Create. No placeholders, no "maybe X". If unsure, omit and put it in openQuestions.
    - Do NOT include test files in plannedChanges unless tests are explicit in the ticket — the Validate step adds tests after Implement.
    - For each open question, populate `suggestions` with 2-4 likely answers when the answer space is small/predictable (e.g. "Prisma" vs "Drizzle", "REST" vs "GraphQL"). When the question is open-ended, leave suggestions as `[]` and the user will free-form answer. Always include `suggestions` (empty array if none).
    - Avoid describing the AC verbatim. If a task description could be lifted from the ticket text without engineering judgment added, rewrite it.
    """;

    private static string BuildRepoAnalysisPrompt(RepoContext ctx)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are analyzing a software repository to produce a high-level summary. A mechanical scan has already been done. Read the scan output below and produce judgment-only fields.");
        sb.AppendLine();
        sb.AppendLine($"Repo name: {ctx.RepoName}");
        sb.AppendLine($"Tech stack: {ctx.TechStack}");
        if (!string.IsNullOrEmpty(ctx.ReadmeSummary))
        {
            sb.AppendLine();
            sb.AppendLine("README excerpt:");
            sb.AppendLine(ctx.ReadmeSummary);
        }
        sb.AppendLine();
        sb.AppendLine("Directory tree:");
        sb.AppendLine(Truncate(ctx.DirectoryTree, 4000));
        if (ctx.EntryPoints.Count > 0)
            sb.AppendLine("Entry points: " + string.Join(", ", ctx.EntryPoints));
        if (ctx.ApiSurface.Count > 0)
        {
            sb.AppendLine("Detected API endpoints (sample):");
            foreach (var ep in ctx.ApiSurface.Take(40))
                sb.AppendLine($"  {ep.Method} {ep.Route} ({ep.File}:{ep.Line})");
        }
        if (ctx.DataModel.Count > 0)
            sb.AppendLine("Data entities: " + string.Join(", ", ctx.DataModel.Take(40).Select(d => d.Name)));
        if (ctx.TopDependencies.Count > 0)
            sb.AppendLine("Top dependencies: " + string.Join(", ", ctx.TopDependencies.Take(20).Select(d => $"{d.Name}@{d.Version}")));
        sb.AppendLine();
        sb.AppendLine("Respond with ONLY a JSON object matching this exact shape — no prose, no code fences:");
        sb.AppendLine("""
        {
          "architectureOverview": "2-3 paragraphs describing what this repo is, its high-level design, and the boundaries between major components",
          "features": [ { "name": "short feature name", "description": "what it does", "relatedFiles": ["path/to/file"] } ],
          "dataFlowDescription": "1-2 paragraphs on how data flows through the system from entry to persistence",
          "businessDomains": ["domain-name-1", "domain-name-2"],
          "codeQualityNotes": ["observation 1", "observation 2"]
        }
        """);
        sb.AppendLine();
        sb.AppendLine("Limit features to the 8-12 most important. Limit codeQualityNotes to 3-6 items. Be concrete and grounded in the scan output.");
        return sb.ToString();
    }

    private static string BuildTicketAnalysisPrompt(Ticket ticket, IReadOnlyList<RepoContext> repoContexts)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are analyzing a development ticket to identify gaps in the requirements, break it into tasks, and surface relevant files.");
        sb.AppendLine();
        AppendTicket(sb, ticket);
        AppendRepoContexts(sb, repoContexts);
        sb.AppendLine();
        sb.AppendLine("Respond with ONLY a JSON object matching this shape — no prose, no code fences:");
        sb.AppendLine("""
        {
          "gaps": [ { "severity": "Error|Warning|Info", "type": "short-id", "detail": "specific concrete missing detail" } ],
          "tasks": [ { "order": 1, "description": "engineering task to do", "fromTicket": true } ],
          "relevantFiles": [ { "path": "path/to/file.cs", "reason": "why this file is likely to change" } ],
          "openQuestions": [ { "text": "question that must be answered before planning can start", "suggestions": ["likely answer A", "likely answer B", "likely answer C"] } ]
        }
        """);
        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("- gaps: only report things actually missing or ambiguous in THIS ticket. Don't pad. Empty list is fine if the ticket is well-specified.");
        sb.AppendLine("- tasks: 3-10 concrete engineering steps a developer can work through. fromTicket=true if it's stated, false if you inferred it.");
        sb.AppendLine("- relevantFiles: only files mentioned above in repo context that you're confident will be touched. Don't guess paths.");
        sb.AppendLine("- openQuestions: convert every Error-severity gap into a question the user can answer. Include 2-4 concrete suggestions when the answer space is predictable; leave suggestions empty for open-ended questions. Empty array if no errors.");
        return sb.ToString();
    }

    private static string BuildRefineAnalysisPrompt(Ticket ticket, TicketAnalysis previous, IReadOnlyList<ChatMessage> conversation, IReadOnlyList<RepoContext> repoContexts)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are refining a previously generated ticket analysis based on the user's feedback. Produce a fresh, complete analysis — not a diff.");
        sb.AppendLine();
        AppendTicket(sb, ticket);
        sb.AppendLine();
        sb.AppendLine("PREVIOUS ANALYSIS:");
        if (previous.Gaps.Count > 0)
        {
            sb.AppendLine("Gaps:");
            foreach (var g in previous.Gaps) sb.AppendLine($"  - [{g.Severity}] {g.Rule}: {g.Detail}");
        }
        if (previous.Tasks.Count > 0)
        {
            sb.AppendLine("Tasks:");
            foreach (var t in previous.Tasks) sb.AppendLine($"  {t.Order}. {t.Description}");
        }
        sb.AppendLine();
        sb.AppendLine("CONVERSATION (chronological — respond to the latest user message):");
        foreach (var m in conversation) sb.AppendLine($"  [{m.Role}] {m.Content}");
        AppendRepoContexts(sb, repoContexts);
        sb.AppendLine();
        sb.AppendLine("Address the user's feedback directly. Drop gaps that the user resolved. Update tasks if the user clarified scope. Respond with ONLY a JSON object — same shape as the original analysis:");
        sb.AppendLine("""
        {
          "gaps": [ { "severity": "Error|Warning|Info", "type": "short-id", "detail": "..." } ],
          "tasks": [ { "order": 1, "description": "...", "fromTicket": true } ],
          "relevantFiles": [ { "path": "...", "reason": "..." } ]
        }
        """);
        return sb.ToString();
    }

    private static string BuildPlanPrompt(Ticket ticket, TicketAnalysis analysis, IReadOnlyList<RepoContext> repoContexts)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a senior engineer producing a CONCRETE implementation plan for a ticket. The plan will be reviewed, approved, then handed to an executor agent that has full repo access.");
        sb.AppendLine();
        AppendTicket(sb, ticket);
        if (analysis.Tasks.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Pre-analysis surfaced these signals from the ticket text (use as INPUT only — do NOT mirror them as your plan tasks):");
            foreach (var t in analysis.Tasks) sb.AppendLine($"  • {t.Description}");
        }
        if (analysis.RelevantFiles.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Pre-analysis flagged these files as likely-touched:");
            foreach (var f in analysis.RelevantFiles.Take(15)) sb.AppendLine($"  - {f.Path}: {f.Reason}");
        }
        AppendRepoContexts(sb, repoContexts);
        sb.AppendLine();
        sb.AppendLine("Respond with ONLY a JSON object — no prose, no code fences:");
        sb.AppendLine("""
        {
          "approach": "1-2 paragraphs: the overall engineering strategy, key design decisions, and the order things have to happen",
          "tasks": [ { "order": 1, "description": "engineering action", "targetFile": "exact/path.ext or null", "rationale": "why this task is required" } ],
          "plannedChanges": [ { "path": "exact/path.ext", "type": "Create|Modify|Delete", "reason": "what specifically changes here" } ],
          "assumptions": ["concrete decision you made that, if wrong, invalidates this plan"],
          "openQuestions": [
            { "text": "question that BLOCKS execution", "suggestions": ["likely answer A", "likely answer B"] }
          ]
        }
        """);
        sb.AppendLine();
        sb.AppendLine(PlanQualityRules);
        return sb.ToString();
    }

    private static string BuildReworkPrompt(Ticket ticket, ImplementationPlan previousPlan, string userFeedback, IReadOnlyList<RepoContext> repoContexts)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are reworking a PREVIOUSLY GENERATED implementation plan based on the user's feedback. Produce a fresh, complete plan — not a diff.");
        sb.AppendLine();
        AppendTicket(sb, ticket);
        sb.AppendLine();
        sb.AppendLine("PREVIOUS PLAN:");
        sb.AppendLine($"Approach: {Truncate(previousPlan.Approach, 1200)}");
        if (previousPlan.Tasks.Count > 0)
        {
            sb.AppendLine("Previous tasks:");
            foreach (var t in previousPlan.Tasks)
            {
                var target = string.IsNullOrEmpty(t.TargetFile) ? "" : $" [{t.TargetFile}]";
                sb.AppendLine($"  {t.Order}. {t.Description}{target}");
                if (!string.IsNullOrEmpty(t.Rationale)) sb.AppendLine($"     rationale: {t.Rationale}");
            }
        }
        if (previousPlan.PlannedChanges.Count > 0)
        {
            sb.AppendLine("Previous planned changes:");
            foreach (var c in previousPlan.PlannedChanges)
                sb.AppendLine($"  {c.Type} {c.Path} — {c.Reason}");
        }
        if (previousPlan.Comments.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Conversation history (chronological):");
            foreach (var c in previousPlan.Comments)
                sb.AppendLine($"  [{c.Type}] {c.Author}: {c.Content}");
        }
        sb.AppendLine();
        sb.AppendLine("LATEST USER FEEDBACK:");
        sb.AppendLine(userFeedback);
        AppendRepoContexts(sb, repoContexts);
        sb.AppendLine();
        sb.AppendLine("Address the feedback directly. Keep what was good in the previous plan; rework what the user objected to. Respond with ONLY a JSON object — same shape as before:");
        sb.AppendLine("""
        {
          "approach": "...",
          "tasks": [ { "order": 1, "description": "...", "targetFile": "...", "rationale": "..." } ],
          "plannedChanges": [ { "path": "...", "type": "Create|Modify|Delete", "reason": "..." } ],
          "assumptions": ["..."],
          "openQuestions": [ { "text": "...", "suggestions": ["..."] } ]
        }
        """);
        sb.AppendLine();
        sb.AppendLine(PlanQualityRules);
        return sb.ToString();
    }

    private static string BuildReviewPrompt(string diff, RepoContext context)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Review this git diff for the {context.RepoName} repo ({context.TechStack}). Identify security issues and code-quality issues.");
        sb.AppendLine();
        sb.AppendLine("DIFF:");
        sb.AppendLine(Truncate(diff, 12000));
        sb.AppendLine();
        sb.AppendLine("Respond with ONLY a JSON object matching this shape — no prose, no code fences:");
        sb.AppendLine("""
        {
          "securityFindings": [ { "severity": "high|medium|low", "type": "short-id", "file": "path", "line": 42, "message": "what's wrong and how to fix" } ],
          "qualityFindings": [ { "type": "short-id", "file": "path", "message": "what's wrong" } ]
        }
        """);
        return sb.ToString();
    }

    private static string BuildAcReviewPrompt(Ticket ticket, string diff, RepoContext context)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Assess whether this code diff satisfies the ticket's acceptance criteria. Behavioral review only — code quality is reviewed separately.");
        sb.AppendLine();
        AppendTicket(sb, ticket);
        sb.AppendLine();
        sb.AppendLine($"Repo: {context.RepoName} ({context.TechStack})");
        sb.AppendLine();
        sb.AppendLine("DIFF:");
        sb.AppendLine(Truncate(diff, 12000));
        sb.AppendLine();
        sb.AppendLine("Respond with ONLY a JSON object — no prose, no code fences:");
        sb.AppendLine("""
        {
          "satisfiesAc": true,
          "concerns": ["specific behavior the AC requires that the diff doesn't provide"],
          "summary": "1-2 sentences describing how the diff addresses the AC"
        }
        """);
        sb.AppendLine();
        sb.AppendLine("Only flag concerns when AC explicitly demands behavior the diff lacks. Vague AC with a plausible implementation should pass. Don't flag missing tests or style — those are separate gates.");
        return sb.ToString();
    }

    private static void AppendTicket(StringBuilder sb, Ticket ticket)
    {
        sb.AppendLine($"Ticket: {ticket.Key}: {ticket.Summary}");
        if (!string.IsNullOrWhiteSpace(ticket.Description))
        {
            sb.AppendLine("Description:");
            sb.AppendLine(Truncate(ticket.Description, 2000));
        }
        if (!string.IsNullOrWhiteSpace(ticket.AcceptanceCriteria))
        {
            sb.AppendLine("Acceptance criteria:");
            sb.AppendLine(Truncate(ticket.AcceptanceCriteria, 1500));
        }
    }

    private static void AppendRepoContexts(StringBuilder sb, IReadOnlyList<RepoContext> repoContexts)
    {
        if (repoContexts.Count == 0) return;
        sb.AppendLine();
        sb.AppendLine("Repository context:");
        foreach (var ctx in repoContexts)
        {
            sb.AppendLine();
            sb.AppendLine($"### {ctx.RepoName} ({ctx.TechStack})");
            if (!string.IsNullOrEmpty(ctx.ArchitectureOverview))
                sb.AppendLine(Truncate(ctx.ArchitectureOverview, 600));
            if (ctx.ApiSurface.Count > 0)
                sb.AppendLine($"Endpoints: {string.Join(", ", ctx.ApiSurface.Take(8).Select(e => $"{e.Method} {e.Route}"))}");
            if (ctx.DataModel.Count > 0)
                sb.AppendLine($"Entities: {string.Join(", ", ctx.DataModel.Take(12).Select(d => d.Name))}");
            if (ctx.EntryPoints.Count > 0)
                sb.AppendLine($"Entry points: {string.Join(", ", ctx.EntryPoints.Take(6))}");
        }
    }

    // ── DTOs (mirror those in ClaudeCliAgent) ─────────────────────────────

    private sealed class RepoAiFields
    {
        public string? ArchitectureOverview { get; set; }
        public List<FeatureDto>? Features { get; set; }
        public string? DataFlowDescription { get; set; }
        public List<string>? BusinessDomains { get; set; }
        public List<string>? CodeQualityNotes { get; set; }
    }
    private sealed class FeatureDto { public string Name { get; set; } = ""; public string? Description { get; set; } public List<string>? RelatedFiles { get; set; } }

    private sealed class TicketAnalysisDto
    {
        public List<GapDto>? Gaps { get; set; }
        public List<TaskDto>? Tasks { get; set; }
        public List<RelevantFileDto>? RelevantFiles { get; set; }
        public List<OpenQuestionDto>? OpenQuestions { get; set; }
    }
    private sealed class GapDto { public string? Severity { get; set; } public string? Type { get; set; } public string? Detail { get; set; } }
    private sealed class TaskDto { public int Order { get; set; } public string? Description { get; set; } public bool FromTicket { get; set; } }
    private sealed class RelevantFileDto { public string? Path { get; set; } public string? Reason { get; set; } }

    private sealed class PlanDto
    {
        public string? Approach { get; set; }
        public List<PlanTaskDto>? Tasks { get; set; }
        public List<PlanChangeDto>? PlannedChanges { get; set; }
        public List<string>? Assumptions { get; set; }
        public List<OpenQuestionDto>? OpenQuestions { get; set; }
    }
    private sealed class PlanTaskDto { public int Order { get; set; } public string? Description { get; set; } public string? TargetFile { get; set; } public string? Rationale { get; set; } }
    private sealed class PlanChangeDto { public string? Path { get; set; } public string? Type { get; set; } public string? Reason { get; set; } }
    private sealed class OpenQuestionDto { public string? Text { get; set; } public List<string>? Suggestions { get; set; } }

    private sealed class ReviewDto
    {
        public List<SecurityFindingDto>? SecurityFindings { get; set; }
        public List<QualityFindingDto>? QualityFindings { get; set; }
    }
    private sealed class SecurityFindingDto { public string? Severity { get; set; } public string? Type { get; set; } public string? File { get; set; } public int? Line { get; set; } public string? Message { get; set; } }
    private sealed class QualityFindingDto { public string? Type { get; set; } public string? File { get; set; } public string? Message { get; set; } }

    private sealed class AcReviewDto
    {
        public bool SatisfiesAc { get; set; } = true;
        public List<string>? Concerns { get; set; }
        public string? Summary { get; set; }
    }
}
