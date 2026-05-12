using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LightsOff.Core.Abstractions;
using LightsOff.Core.Domain;
using Microsoft.Extensions.Logging;

namespace LightsOff.Infrastructure.Agents;

/// <summary>
/// Uses the host's local <c>claude</c> CLI as the agent backend. Two execution modes:
///
/// <list type="bullet">
/// <item><b>Streaming session</b> — <see cref="StartSessionAsync"/> spawns
/// <c>claude --print --input-format stream-json --output-format stream-json</c> and bridges it
/// through <see cref="SubprocessAgentSession"/>. Used by the Implement step.</item>
/// <item><b>One-shot text-in/JSON-out</b> — Analyze, Plan, Review use <see cref="ClaudeCliOneShot"/>
/// to invoke <c>claude -p</c> with a structured prompt and parse the assistant's JSON reply
/// into the relevant domain type.</item>
/// </list>
///
/// Availability: <c>which claude</c> succeeds AND <c>~/.claude/</c> exists. Reuses the host's
/// existing login — no API key, no container mounts.
/// </summary>
public sealed class ClaudeCliAgent : ICodeAgent
{
    public const string AgentId = "claude-cli";

    private readonly LocalCodeAgent _fallback;
    private readonly ILogger<ClaudeCliAgent> _logger;
    private readonly Lazy<string?> _claudePath;

    public ClaudeCliAgent(LocalCodeAgent fallback, ILogger<ClaudeCliAgent> logger)
    {
        _fallback = fallback;
        _logger = logger;
        _claudePath = new Lazy<string?>(ResolveClaudePath);
    }

    public string Id => AgentId;
    public string DisplayName => "Claude Code CLI (host)";
    public bool RequiresApiKey => false;

    public bool IsAvailable
    {
        get
        {
            if (_claudePath.Value is null) return false;
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home)) return false;
            return Directory.Exists(Path.Combine(home, ".claude"));
        }
    }

    private ClaudeCliOneShot OneShot(TimeSpan? timeout = null) => new(_claudePath.Value!, _logger, timeout);

    // Plan generation/rework prompts are heavier than analysis — give them more headroom.
    private static readonly TimeSpan PlanTimeout = TimeSpan.FromMinutes(10);

    // ── Repo analysis: local first, AI enriches judgment fields ──────────

    public async Task<RepoContext> AnalyzeRepositoryAsync(string repoPath, RepoContext? existingContext = null, CancellationToken ct = default)
    {
        EnsureAvailable();

        // 1. Mechanical scan via local — gives us the directory tree, endpoints, entities, etc.
        var ctx = await _fallback.AnalyzeRepositoryAsync(repoPath, existingContext, ct).ConfigureAwait(false);
        ctx.AgentUsed = Id;

        // 2. Ask claude to fill the AI-only fields based on the local summary. Cheap (small
        //    context) and avoids re-scanning the filesystem.
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
            // Repo analysis is enrichment, not a primary signal — log and keep the local result.
            _logger.LogWarning(ex, "claude-cli repo enrichment failed for {Repo}; returning local-only context", ctx.RepoName);
        }

        RepoContextSerializer.Save(repoPath, ctx);
        return ctx;
    }

    // ── Ticket analysis: AI is the primary signal ─────────────────────────

    public async Task<TicketAnalysis> AnalyzeTicketAsync(Ticket ticket, IReadOnlyList<RepoContext> repoContexts, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        EnsureAvailable();

        var prompt = BuildTicketAnalysisPrompt(ticket, repoContexts);
        var ai = await OneShot().RunForJsonAsync<TicketAnalysisDto>(prompt, progress, ct: ct).ConfigureAwait(false);

        return new TicketAnalysis
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
    }

    // ── Plan generation: AI is the primary signal ─────────────────────────

    public async Task<ImplementationPlan> GeneratePlanAsync(Ticket ticket, TicketAnalysis analysis, IReadOnlyList<RepoContext> repoContexts, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        EnsureAvailable();

        var prompt = BuildPlanPrompt(ticket, analysis, repoContexts);
        var ai = await OneShot(PlanTimeout).RunForJsonAsync<PlanDto>(prompt, progress, ct: ct).ConfigureAwait(false);

        return new ImplementationPlan
        {
            TicketKey = ticket.Key,
            AgentUsed = Id,
            Status = PlanStatus.Draft,
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
            OpenQuestions = MapOpenQuestions(ai.OpenQuestions)
        };
    }

    // ── Code review: AI semantic review of the diff ───────────────────────

    public async Task<CodeReview> ReviewChangesAsync(string diff, RepoContext context, CancellationToken ct = default)
    {
        EnsureAvailable();
        if (string.IsNullOrWhiteSpace(diff))
        {
            return new CodeReview { AgentUsed = Id };
        }

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

    // ── Mechanical: keep delegated to local ───────────────────────────────

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

    // ── Streaming session (Implement step) ────────────────────────────────

    public Task<IAgentSession> StartSessionAsync(AgentSessionSpec spec, CancellationToken ct = default)
    {
        EnsureAvailable();
        if (spec.AllowedRepoPaths.Count == 0)
            throw new InvalidOperationException("AgentSessionSpec must include at least one allowed repo path.");

        var cwd = spec.AllowedRepoPaths[0];
        if (!Directory.Exists(cwd))
            throw new DirectoryNotFoundException($"Session CWD not found on host: {cwd}");

        var sessionId = Guid.NewGuid().ToString("N");
        var systemPrompt = BuildSessionSystemPrompt(spec);
        var psi = new ProcessStartInfo
        {
            FileName = _claudePath.Value!,
            WorkingDirectory = cwd,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("--print");
        psi.ArgumentList.Add("--verbose"); // claude requires --verbose when --print is paired with stream-json output
        psi.ArgumentList.Add("--input-format"); psi.ArgumentList.Add("stream-json");
        psi.ArgumentList.Add("--output-format"); psi.ArgumentList.Add("stream-json");
        psi.ArgumentList.Add("--include-partial-messages");
        psi.ArgumentList.Add("--permission-mode"); psi.ArgumentList.Add("acceptEdits");
        psi.ArgumentList.Add("--append-system-prompt"); psi.ArgumentList.Add(systemPrompt);
        foreach (var extra in spec.AllowedRepoPaths.Skip(1))
        {
            psi.ArgumentList.Add("--add-dir");
            psi.ArgumentList.Add(extra);
        }

        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start claude process.");

        _logger.LogInformation(
            "claude-cli session {SessionId} started (pid={Pid}, cwd={Cwd}, ticket={Ticket}, mode={Mode})",
            sessionId, proc.Id, cwd, spec.TicketKey, spec.Mode);

        IAgentSession session = new SubprocessAgentSession(
            sessionId, Id, proc, _logger,
            parseLine: ClaudeStreamJsonParser.Parse,
            serializeInput: ClaudeStreamJsonParser.SerializeInput);
        return Task.FromResult(session);
    }

    public Task<ChatReplyEvent> ReplyToPlanChatAsync(ImplementationPlan plan, string userMessage, CancellationToken ct = default)
    {
        EnsureAvailable();
        throw new NotSupportedException(
            "Plan-chat replies via claude-cli are scheduled for Phase 2. Use Request Changes to rework the plan instead.");
    }

    public async Task<TicketAnalysis> RefineAnalysisAsync(Ticket ticket, TicketAnalysis previousAnalysis, IReadOnlyList<ChatMessage> conversation, IReadOnlyList<RepoContext> repoContexts, CancellationToken ct = default)
    {
        EnsureAvailable();
        var prompt = BuildRefineAnalysisPrompt(ticket, previousAnalysis, conversation, repoContexts);
        var ai = await OneShot().RunForJsonAsync<TicketAnalysisDto>(prompt, ct: ct).ConfigureAwait(false);

        return new TicketAnalysis
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
            // Carry conversation forward so the thread persists across refinements.
            Conversation = conversation.ToList()
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

    public async Task<ImplementationPlan> ReworkPlanAsync(Ticket ticket, ImplementationPlan previousPlan, string userFeedback, IReadOnlyList<RepoContext> repoContexts, CancellationToken ct = default)
    {
        EnsureAvailable();
        var prompt = BuildReworkPrompt(ticket, previousPlan, userFeedback, repoContexts);
        var ai = await OneShot(PlanTimeout).RunForJsonAsync<PlanDto>(prompt, ct: ct).ConfigureAwait(false);

        return new ImplementationPlan
        {
            TicketKey = ticket.Key,
            AgentUsed = Id,
            Status = PlanStatus.Draft,
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
            // Carry comments forward so the conversation history is preserved across reworks.
            Comments = previousPlan.Comments.ToList()
        };
    }

    // ── Prompt builders ───────────────────────────────────────────────────

    private static string BuildRepoAnalysisPrompt(RepoContext ctx)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are analyzing a software repository to produce a high-level summary that will be reused across many ticket runs.");
        sb.AppendLine("A mechanical scan has already been done. Read the scan output below and produce judgment-only fields a reader needs to grasp what the repo IS and DOES.");
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
        {
            sb.AppendLine();
            sb.AppendLine("Entry points: " + string.Join(", ", ctx.EntryPoints));
        }
        if (ctx.ApiSurface.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Detected API endpoints (sample):");
            foreach (var ep in ctx.ApiSurface.Take(40))
                sb.AppendLine($"  {ep.Method} {ep.Route} ({ep.File}:{ep.Line})");
        }
        if (ctx.DataModel.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Data entities: " + string.Join(", ", ctx.DataModel.Take(40).Select(d => d.Name)));
        }
        if (ctx.TopDependencies.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Top dependencies: " + string.Join(", ", ctx.TopDependencies.Take(20).Select(d => $"{d.Name}@{d.Version}")));
        }
        sb.AppendLine();
        sb.AppendLine("Respond with ONLY a JSON object matching this exact shape — no prose, no code fences:");
        sb.AppendLine("""
        {
          "architectureOverview": "2-3 paragraphs describing what this repo is, its high-level design, and the boundaries between major components",
          "features": [
            { "name": "short feature name", "description": "what it does", "relatedFiles": ["path/to/file"] }
          ],
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
        sb.AppendLine($"Ticket: {ticket.Key}");
        sb.AppendLine($"Summary: {ticket.Summary}");
        if (!string.IsNullOrWhiteSpace(ticket.Description))
        {
            sb.AppendLine();
            sb.AppendLine("Description:");
            sb.AppendLine(Truncate(ticket.Description, 3000));
        }
        if (!string.IsNullOrWhiteSpace(ticket.AcceptanceCriteria))
        {
            sb.AppendLine();
            sb.AppendLine("Acceptance criteria:");
            sb.AppendLine(Truncate(ticket.AcceptanceCriteria, 2000));
        }
        if (ticket.Labels?.Count > 0)
            sb.AppendLine($"Labels: {string.Join(", ", ticket.Labels)}");

        if (repoContexts.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Repository context (summarized):");
            foreach (var ctx in repoContexts)
            {
                sb.AppendLine();
                sb.AppendLine($"### {ctx.RepoName} ({ctx.TechStack})");
                if (!string.IsNullOrEmpty(ctx.ArchitectureOverview))
                    sb.AppendLine(Truncate(ctx.ArchitectureOverview, 800));
                if (ctx.Features is { Count: > 0 })
                {
                    sb.AppendLine("Features:");
                    foreach (var f in ctx.Features.Take(8))
                        sb.AppendLine($"  - {f.Name}: {f.Description}");
                }
                if (ctx.ApiSurface.Count > 0)
                    sb.AppendLine($"Endpoints (sample): {string.Join(", ", ctx.ApiSurface.Take(10).Select(e => $"{e.Method} {e.Route}"))}");
                if (ctx.DataModel.Count > 0)
                    sb.AppendLine($"Entities: {string.Join(", ", ctx.DataModel.Take(15).Select(d => d.Name))}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("Respond with ONLY a JSON object matching this shape — no prose, no code fences:");
        sb.AppendLine("""
        {
          "gaps": [
            { "severity": "Error|Warning|Info", "type": "short-id", "detail": "specific concrete missing detail" }
          ],
          "tasks": [
            { "order": 1, "description": "engineering task to do", "fromTicket": true }
          ],
          "relevantFiles": [
            { "path": "path/to/file.cs", "reason": "why this file is likely to change" }
          ],
          "openQuestions": [
            { "text": "question that must be answered before planning can start", "suggestions": ["likely answer A", "likely answer B", "likely answer C"] }
          ]
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
        sb.AppendLine("You are refining a previously generated ticket analysis based on the user's feedback. The user is clarifying gaps, answering open questions, or pushing back on findings. Produce a fresh, complete analysis — not a diff.");
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
            foreach (var t in previous.Tasks) sb.AppendLine($"  {t.Order}. {t.Description}{(t.FromTicket ? " (from ticket)" : "")}");
        }
        if (previous.RelevantFiles.Count > 0)
        {
            sb.AppendLine("Relevant files:");
            foreach (var f in previous.RelevantFiles.Take(15)) sb.AppendLine($"  - {f.Path}: {f.Reason}");
        }

        sb.AppendLine();
        sb.AppendLine("CONVERSATION (chronological — respond to the latest user message):");
        foreach (var m in conversation)
            sb.AppendLine($"  [{m.Role}] {m.Content}");

        AppendRepoContexts(sb, repoContexts);

        sb.AppendLine();
        sb.AppendLine("Address the user's feedback directly. Drop gaps that the user resolved (keep only ones still open). Update tasks if the user clarified scope. Respond with ONLY a JSON object — same shape as the original analysis:");
        sb.AppendLine("""
        {
          "gaps": [ { "severity": "Error|Warning|Info", "type": "short-id", "detail": "..." } ],
          "tasks": [ { "order": 1, "description": "...", "fromTicket": true } ],
          "relevantFiles": [ { "path": "...", "reason": "..." } ]
        }
        """);
        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("- If the user answered an Error gap, remove it from gaps. Don't carry resolved issues forward.");
        sb.AppendLine("- If the user clarified ambiguity, the resulting tasks should be more specific, not the same as before.");
        sb.AppendLine("- Empty arrays are fine when nothing of that kind applies.");
        return sb.ToString();
    }

    private static string BuildPlanPrompt(Ticket ticket, TicketAnalysis analysis, IReadOnlyList<RepoContext> repoContexts)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a senior engineer producing a CONCRETE implementation plan for a ticket. The plan will be reviewed, approved, then handed to an executor agent that has full repo access.");
        sb.AppendLine();
        AppendTicket(sb, ticket);
        AppendAnalysisSignals(sb, analysis);

        sb.AppendLine();
        sb.AppendLine("Respond with ONLY a JSON object — no prose, no code fences:");
        sb.AppendLine("""
        {
          "approach": "1-2 paragraphs: the overall engineering strategy, key design decisions, and the order things have to happen",
          "tasks": [
            { "order": 1, "description": "engineering action", "targetFile": "exact/path.ext or null for cross-cutting", "rationale": "why this task is required" }
          ],
          "plannedChanges": [
            { "path": "exact/path.ext", "type": "Create|Modify|Delete", "reason": "what specifically changes here" }
          ],
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
        sb.AppendLine("PREVIOUS PLAN (the one the user is asking you to rework):");
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
            sb.AppendLine("Conversation history (chronological — the user has previously said):");
            foreach (var c in previousPlan.Comments)
                sb.AppendLine($"  [{c.Type}] {c.Author}: {c.Content}");
        }

        sb.AppendLine();
        sb.AppendLine("LATEST USER FEEDBACK (this is the change request to address):");
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

    /// <summary>
    /// Shared rules for plan generation and rework. Centralizing this keeps both prompts in lockstep
    /// when we tighten anti-checklist guidance.
    /// </summary>
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

    private static void AppendAnalysisSignals(StringBuilder sb, TicketAnalysis analysis)
    {
        if (analysis.Tasks.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Pre-analysis surfaced these signals from the ticket text (use as INPUT only — do NOT mirror them as your plan tasks):");
            foreach (var t in analysis.Tasks)
                sb.AppendLine($"  • {t.Description}");
        }
        if (analysis.RelevantFiles.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Pre-analysis flagged these files as likely-touched:");
            foreach (var f in analysis.RelevantFiles.Take(15))
                sb.AppendLine($"  - {f.Path}: {f.Reason}");
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

    private static string BuildAcReviewPrompt(Ticket ticket, string diff, RepoContext context)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Assess whether this code diff actually satisfies the ticket's acceptance criteria. This is a behavioral review — focus on whether the AC is met, not on code quality (a separate review handles that).");
        sb.AppendLine();
        sb.AppendLine($"Ticket: {ticket.Key}: {ticket.Summary}");
        if (!string.IsNullOrWhiteSpace(ticket.Description))
        {
            sb.AppendLine("Description:");
            sb.AppendLine(Truncate(ticket.Description, 1500));
        }
        if (!string.IsNullOrWhiteSpace(ticket.AcceptanceCriteria))
        {
            sb.AppendLine("Acceptance criteria:");
            sb.AppendLine(Truncate(ticket.AcceptanceCriteria, 1500));
        }
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
        sb.AppendLine("Rules:");
        sb.AppendLine("- Only flag concerns when the AC explicitly demands behavior that the diff demonstrably lacks. Vague AC ('the user can do X') with a plausible implementation should pass.");
        sb.AppendLine("- Do NOT flag missing tests as a concern — tests are gated separately.");
        sb.AppendLine("- Do NOT flag style or readability — those are handled by the code review.");
        sb.AppendLine("- An empty concerns array is the right answer when the AC is met.");
        return sb.ToString();
    }

    private sealed class AcReviewDto
    {
        public bool SatisfiesAc { get; set; } = true;
        public List<string>? Concerns { get; set; }
        public string? Summary { get; set; }
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
          "securityFindings": [
            { "severity": "high|medium|low", "type": "short-id", "file": "path", "line": 42, "message": "what's wrong and how to fix" }
          ],
          "qualityFindings": [
            { "type": "short-id", "file": "path", "message": "what's wrong" }
          ]
        }
        """);
        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("- Be specific. Include the line number when possible.");
        sb.AppendLine("- Security: SQL injection, XSS, hardcoded secrets, auth bypass, path traversal, deserialization, SSRF, unsafe regex, etc.");
        sb.AppendLine("- Quality: dead code, unused imports, naming issues, missing error handling at boundaries, debug output left in, TODO/FIXME markers added.");
        sb.AppendLine("- Empty arrays are fine when the diff is clean.");
        return sb.ToString();
    }

    private static string BuildSessionSystemPrompt(AgentSessionSpec spec)
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
        sb.AppendLine("IMPORTANT: Do NOT run build, test, or package-install commands with your Bash tool.");
        sb.AppendLine("The lights-off pipeline runs those inside the dev container and will report results back to you.");
        sb.AppendLine("Your Bash tool may be used for read-only commands: ls, cat, git log, git diff, rg, grep.");
        sb.AppendLine();
        sb.AppendLine("When you finish the task list, summarize what you changed and wait for the next input.");
        return sb.ToString();
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private void EnsureAvailable()
    {
        if (!IsAvailable)
        {
            var hint = _claudePath.Value is null
                ? "`claude` not found on PATH — install Claude Code on your host."
                : "`~/.claude/` not found — run `claude login` once on your host.";
            throw new InvalidOperationException(
                $"Claude Code CLI agent is not available. {hint}");
        }
    }

    private static string? ResolveClaudePath()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var sep = OperatingSystem.IsWindows() ? ';' : ':';
        foreach (var dir in path.Split(sep, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, OperatingSystem.IsWindows() ? "claude.exe" : "claude");
            if (File.Exists(candidate)) return candidate;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] fallbacks =
        {
            Path.Combine(home, ".local", "bin", "claude"),
            Path.Combine(home, ".npm-global", "bin", "claude"),
            "/opt/homebrew/bin/claude",
            "/usr/local/bin/claude"
        };
        return fallbacks.FirstOrDefault(File.Exists);
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

    // ── DTOs for JSON deserialization (keeps domain types decoupled from wire shape) ──

    private sealed class RepoAiFields
    {
        public string? ArchitectureOverview { get; set; }
        public List<FeatureDto>? Features { get; set; }
        public string? DataFlowDescription { get; set; }
        public List<string>? BusinessDomains { get; set; }
        public List<string>? CodeQualityNotes { get; set; }
    }
    private sealed class FeatureDto
    {
        public string Name { get; set; } = "";
        public string? Description { get; set; }
        public List<string>? RelatedFiles { get; set; }
    }

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
}
