using System.Text;
using System.Text.Json;
using LightsOff.Core.Abstractions;
using LightsOff.Core.Configuration;
using LightsOff.Core.Domain;
using LightsOff.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LightsOff.Infrastructure.Pipeline;

/// <summary>
/// Drives a pipeline step end-to-end: loads persisted state, invokes the right agent / docker /
/// git operations, streams events to the runner via <see cref="StepRunContext.EmitEvent"/>,
/// returns a terminal <see cref="StepRun"/>.
///
/// Phase 2: handles Implement, Validate, and Submit (Submit folds Commit + push + PR open).
/// Analyze and Plan continue to run via direct agent calls from the page; the orchestrator only
/// owns the steps that involve docker/git side effects.
/// </summary>
public sealed class PipelineOrchestrator : IPipelineOrchestrator
{
    // The page persists workflow state with Web defaults (camelCase). We MUST use the same
    // settings on the read side or every property comes back null on case-sensitive default.
    private static readonly JsonSerializerOptions PersistOptions = new(JsonSerializerDefaults.Web);

    private const int MaxAutoFixRetries = 2;
    private const double NewCodeCoverageThreshold = 80d;

    private readonly AppDbContext _db;
    private readonly ICodeAgentProvider _agents;
    private readonly IOptions<LightsOffOptions> _options;
    private readonly IDockerService _docker;
    private readonly IGitService _git;
    private readonly IWorkspaceManager _workspaces;
    private readonly IStepSessionSink _sessionSink;
    private readonly ILogger<PipelineOrchestrator> _logger;

    public PipelineOrchestrator(
        AppDbContext db,
        ICodeAgentProvider agents,
        IOptions<LightsOffOptions> options,
        IDockerService docker,
        IGitService git,
        IWorkspaceManager workspaces,
        IStepSessionSink sessionSink,
        ILogger<PipelineOrchestrator> logger)
    {
        _db = db;
        _agents = agents;
        _options = options;
        _docker = docker;
        _git = git;
        _workspaces = workspaces;
        _sessionSink = sessionSink;
        _logger = logger;
    }

    public async Task<StepRun> RunStepAsync(string ticketKey, PipelineStep step, StepRunContext ctx, CancellationToken ct = default) => step switch
    {
        PipelineStep.Implement => await RunImplementAsync(ticketKey, ctx, ct).ConfigureAwait(false),
        PipelineStep.Validate  => await RunValidateAsync(ticketKey, ctx, ct).ConfigureAwait(false),
        PipelineStep.Submit    => await RunSubmitAsync(ticketKey, ctx, ct).ConfigureAwait(false),
        _ => throw new NotImplementedException(
            $"Step '{step}' is not orchestrated. Analyze and Plan run via direct agent calls; Review is folded into Validate.")
    };

    // ── Implement ─────────────────────────────────────────────────────────

    private async Task<StepRun> RunImplementAsync(string ticketKey, StepRunContext ctx, CancellationToken ct)
    {
        var agent = _agents.GetAgent(ctx.AgentId);
        await EmitLog(ctx, $"[orchestrator] preparing Implement run for {ticketKey} via agent '{ctx.AgentId}'");

        var (workspace, plan, ticket, allowedPaths) = await LoadRunStateAsync(ticketKey, requireApprovedPlan: true, ct).ConfigureAwait(false);
        await EmitLog(ctx, $"[orchestrator] approved plan loaded — {plan!.Tasks.Count} task(s), {plan.PlannedChanges.Count} planned change(s)");
        await EmitLog(ctx, $"[orchestrator] cwd={allowedPaths[0]}{(allowedPaths.Count > 1 ? $" (+{allowedPaths.Count - 1} extra)" : "")}");
        await EmitLog(ctx, "[orchestrator] starting claude session…");

        var spec = new AgentSessionSpec(
            TicketKey: ticketKey,
            Ticket: ticket,
            Plan: plan,
            RepoContexts: Array.Empty<RepoContext>(),
            WorkspaceRoot: _options.Value.WorkspacesRoot,
            AllowedRepoPaths: allowedPaths,
            ContainerId: workspace.ContainerId,
            Mode: AgentSessionMode.Implement);

        await using var session = await agent.StartSessionAsync(spec, ct).ConfigureAwait(false);
        _sessionSink.AttachSession(ticketKey, session);

        await session.SendAsync(new AgentTaskInput(
            Instruction: "Begin implementing the approved plan. Work through the tasks in order. " +
                         "When all tasks are done, summarize your changes and stop.",
            AllowedPaths: allowedPaths), ct).ConfigureAwait(false);

        var (success, error) = await PumpSessionAsync(session, ctx, ct).ConfigureAwait(false);

        return new StepRun
        {
            Id = ctx.StepRunId,
            TicketKey = ticketKey,
            Step = PipelineStep.Implement,
            Status = success ? StepRunStatus.Succeeded : (ct.IsCancellationRequested ? StepRunStatus.Cancelled : StepRunStatus.Failed),
            AgentId = ctx.AgentId,
            StartedAt = DateTimeOffset.UtcNow,
            FinishedAt = DateTimeOffset.UtcNow,
            CancellationReason = error
        };
    }

    // ── Validate (tests + coverage gate + AC review warning + auto-fix loop) ──

    private async Task<StepRun> RunValidateAsync(string ticketKey, StepRunContext ctx, CancellationToken ct)
    {
        var agent = _agents.GetAgent(ctx.AgentId);
        await EmitLog(ctx, $"[orchestrator] preparing Validate run for {ticketKey}");

        var (workspace, plan, ticket, allowedPaths) = await LoadRunStateAsync(ticketKey, requireApprovedPlan: false, ct).ConfigureAwait(false);

        if (string.IsNullOrEmpty(workspace.ContainerId))
            throw new InvalidOperationException("Workspace container is not running — start the workspace before Validate.");

        var configs = await _db.RepoTestConfigs
            .Where(c => workspace.Links.Select(l => l.RepositoryName).Contains(c.RepositoryName))
            .OrderBy(c => c.Order)
            .ToListAsync(ct).ConfigureAwait(false);

        if (configs.Count == 0)
        {
            await EmitLog(ctx, "[validate] no test configs found for any linked repo — Validate cannot run. Configure test commands in the repo's settings.");
            return new StepRun
            {
                Id = ctx.StepRunId,
                TicketKey = ticketKey,
                Step = PipelineStep.Validate,
                Status = StepRunStatus.Failed,
                AgentId = ctx.AgentId,
                StartedAt = DateTimeOffset.UtcNow,
                FinishedAt = DateTimeOffset.UtcNow,
                CancellationReason = "No test configs configured."
            };
        }

        bool succeeded = false;
        string? failureReason = null;
        for (var attempt = 0; attempt <= MaxAutoFixRetries && !succeeded && !ct.IsCancellationRequested; attempt++)
        {
            await EmitLog(ctx, attempt == 0
                ? $"[validate] running {configs.Count} test config(s)…"
                : $"[validate] retry {attempt}/{MaxAutoFixRetries}: re-running tests after auto-fix");

            var tests = await RunTestsAsync(workspace, configs, ctx, ct).ConfigureAwait(false);
            var coverage = await SafeMeasureCoverageAsync(agent, allowedPaths[0], workspace, ctx, ct).ConfigureAwait(false);

            await EmitGateSummary(ctx, tests, coverage);

            if (tests.AllPassed && CoveragePassed(coverage))
            {
                succeeded = true;
                break;
            }

            if (attempt == MaxAutoFixRetries)
            {
                failureReason = BuildFailureReason(tests, coverage);
                await EmitLog(ctx, $"[validate] auto-fix budget exhausted ({MaxAutoFixRetries} retries). Manual intervention needed.");
                break;
            }

            // Spawn a fresh fix session — separate from the Implement session, simpler protocol.
            await EmitLog(ctx, "[validate] sending failures to claude for auto-fix…");
            await RunFixAttemptAsync(agent, ticket, plan, allowedPaths, workspace, tests, coverage, ctx, ct).ConfigureAwait(false);
        }

        // AC review is a warning surface, not a gate. Run it once at the end, on the final diff.
        if (succeeded)
        {
            await EmitAcReviewWarningsAsync(agent, ticket, allowedPaths, workspace, ctx, ct).ConfigureAwait(false);
        }

        return new StepRun
        {
            Id = ctx.StepRunId,
            TicketKey = ticketKey,
            Step = PipelineStep.Validate,
            Status = succeeded ? StepRunStatus.Succeeded : (ct.IsCancellationRequested ? StepRunStatus.Cancelled : StepRunStatus.Failed),
            AgentId = ctx.AgentId,
            StartedAt = DateTimeOffset.UtcNow,
            FinishedAt = DateTimeOffset.UtcNow,
            CancellationReason = failureReason
        };
    }

    private async Task<TestSummary> RunTestsAsync(Workspace workspace, List<RepoTestConfig> configs, StepRunContext ctx, CancellationToken ct)
    {
        var failures = new List<TestFailure>();
        var allPassed = true;

        foreach (var config in configs)
        {
            var commands = (config.Command ?? string.Empty)
                .Split('\n', StringSplitOptions.TrimEntries)
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .ToList();
            if (commands.Count == 0) continue;

            var workDir = string.IsNullOrWhiteSpace(config.WorkingDir)
                ? $"/workspace/{config.RepositoryName}"
                : $"/workspace/{config.RepositoryName}/{config.WorkingDir.Trim('/')}";

            foreach (var cmd in commands)
            {
                await ctx.EmitEvent(new ToolCallEvent(
                    DateTimeOffset.UtcNow,
                    "Test",
                    JsonSerializer.Serialize(new { config = config.Name, repo = config.RepositoryName, command = cmd })));

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromMinutes(10));

                var output = new StringBuilder();
                int exitCode;
                try
                {
                    exitCode = await _docker.ExecStreamAsync(
                        workspace.ContainerId!,
                        new[] { "sh", "-c", $"cd {workDir} && {cmd}" },
                        async chunk =>
                        {
                            output.Append(chunk);
                            await ctx.EmitEvent(new LogChunkEvent(DateTimeOffset.UtcNow, "stdout", chunk));
                        },
                        cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    exitCode = -1;
                    output.AppendLine("\n⏱ Timed out after 10 minutes.");
                    await EmitLog(ctx, "[validate] command timed out");
                }

                if (exitCode != 0)
                {
                    allPassed = false;
                    failures.Add(new TestFailure(config.Name, config.RepositoryName, cmd, exitCode, Tail(output.ToString(), 4000)));
                    await EmitLog(ctx, $"[validate] FAIL ({config.Name}): exit code {exitCode}");
                    break; // stop further commands in this config
                }
                await EmitLog(ctx, $"[validate] PASS ({config.Name}: {cmd})");
            }
        }

        return new TestSummary(allPassed, failures);
    }

    private async Task<CoverageReport?> SafeMeasureCoverageAsync(ICodeAgent agent, string repoPath, Workspace workspace, StepRunContext ctx, CancellationToken ct)
    {
        try
        {
            var defaultBranch = await ResolveDefaultBranchAsync(workspace, repoPath, ct).ConfigureAwait(false);
            var report = await agent.MeasureNewCodeCoverageAsync(repoPath, defaultBranch, ct).ConfigureAwait(false);
            return report;
        }
        catch (Exception ex)
        {
            await EmitLog(ctx, $"[validate] coverage measurement skipped: {ex.Message}");
            return null;
        }
    }

    private static bool CoveragePassed(CoverageReport? report)
    {
        // No artifact / measurement error → warn-only, don't block. Repos without coverage tooling
        // configured shouldn't be blocked from the autonomous flow.
        if (report is null || !string.IsNullOrEmpty(report.Error)) return true;
        return report.NewCodeLinePercent >= NewCodeCoverageThreshold;
    }

    private static Task EmitGateSummary(StepRunContext ctx, TestSummary tests, CoverageReport? coverage)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[validate] tests: {(tests.AllPassed ? "PASS" : $"FAIL ({tests.Failures.Count} failure(s))")}");
        if (coverage is null || !string.IsNullOrEmpty(coverage.Error))
        {
            sb.AppendLine($"[validate] coverage: not measured ({coverage?.Error ?? "no artifact"}) — gate skipped");
        }
        else
        {
            var passed = coverage.NewCodeLinePercent >= NewCodeCoverageThreshold;
            sb.AppendLine($"[validate] coverage: {coverage.NewCodeLinePercent:F1}% on new code ({coverage.NewCodeLinesCovered}/{coverage.NewCodeLinesTotal} lines) — gate {(passed ? "PASS" : "FAIL")}");
        }
        return EmitLog(ctx, sb.ToString().TrimEnd());
    }

    private static string BuildFailureReason(TestSummary tests, CoverageReport? coverage)
    {
        var parts = new List<string>();
        if (!tests.AllPassed) parts.Add($"{tests.Failures.Count} test failure(s)");
        if (coverage is not null && string.IsNullOrEmpty(coverage.Error) && coverage.NewCodeLinePercent < NewCodeCoverageThreshold)
            parts.Add($"coverage {coverage.NewCodeLinePercent:F1}% < {NewCodeCoverageThreshold}%");
        return parts.Count == 0 ? "Validate failed." : string.Join("; ", parts);
    }

    private async Task RunFixAttemptAsync(
        ICodeAgent agent,
        Ticket ticket,
        ImplementationPlan? plan,
        IReadOnlyList<string> allowedPaths,
        Workspace workspace,
        TestSummary tests,
        CoverageReport? coverage,
        StepRunContext ctx,
        CancellationToken ct)
    {
        // Spawn a fresh session in Validate mode; system prompt carries ticket + plan + the
        // failure summary so claude has full context for the fix.
        var spec = new AgentSessionSpec(
            TicketKey: ticket.Key,
            Ticket: ticket,
            Plan: plan,
            RepoContexts: Array.Empty<RepoContext>(),
            WorkspaceRoot: _options.Value.WorkspacesRoot,
            AllowedRepoPaths: allowedPaths,
            ContainerId: workspace.ContainerId,
            Mode: AgentSessionMode.Validate);

        await using var session = await agent.StartSessionAsync(spec, ct).ConfigureAwait(false);
        _sessionSink.AttachSession(ticket.Key, session);

        await session.SendAsync(new AgentTaskInput(
            Instruction: BuildFixInstruction(tests, coverage),
            AllowedPaths: allowedPaths), ct).ConfigureAwait(false);

        await PumpSessionAsync(session, ctx, ct).ConfigureAwait(false);
    }

    private static string BuildFixInstruction(TestSummary tests, CoverageReport? coverage)
    {
        var sb = new StringBuilder();
        sb.AppendLine("The validation gate failed. Fix the issues below, then stop. Do NOT run tests yourself — the lights-off pipeline will re-run them after you finish.");
        sb.AppendLine();
        if (!tests.AllPassed)
        {
            sb.AppendLine($"Test failures ({tests.Failures.Count}):");
            foreach (var f in tests.Failures.Take(10))
            {
                sb.AppendLine($"- [{f.RepositoryName}] config '{f.ConfigName}', command '{f.Command}', exit code {f.ExitCode}");
                sb.AppendLine("  output tail:");
                sb.AppendLine(IndentLines(f.OutputTail, "    "));
            }
        }
        if (coverage is not null && string.IsNullOrEmpty(coverage.Error) && coverage.NewCodeLinePercent < NewCodeCoverageThreshold)
        {
            sb.AppendLine();
            sb.AppendLine($"Coverage on new code is {coverage.NewCodeLinePercent:F1}% (threshold {NewCodeCoverageThreshold}%). Add tests for the uncovered new lines.");
            foreach (var f in coverage.Files.Where(f => f.UncoveredNewLines.Count > 0).Take(20))
            {
                sb.AppendLine($"  - {f.Path}: missing lines {string.Join(", ", f.UncoveredNewLines.Take(20))}");
            }
        }
        sb.AppendLine();
        sb.AppendLine("When you're done with the fix, summarize what you changed and stop.");
        return sb.ToString();
    }

    private async Task EmitAcReviewWarningsAsync(ICodeAgent agent, Ticket ticket, IReadOnlyList<string> allowedPaths, Workspace workspace, StepRunContext ctx, CancellationToken ct)
    {
        try
        {
            // Use the first repo for the AC review — multi-repo is uncommon; single AI call keeps
            // costs low and the review focused.
            var repoPath = allowedPaths[0];
            var defaultBranch = await ResolveDefaultBranchAsync(workspace, repoPath, ct).ConfigureAwait(false);
            var diff = await _git.GetDiffAsync(repoPath, defaultBranch, "HEAD", ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(diff))
            {
                await EmitLog(ctx, "[validate] AC review skipped: no diff against default branch.");
                return;
            }
            var ctxRepo = new RepoContext { RepoName = Path.GetFileName(repoPath), TechStack = "(unknown)" };
            var review = await agent.ReviewAgainstAcAsync(ticket, diff, ctxRepo, ct).ConfigureAwait(false);
            if (review.SatisfiesAc && review.Concerns.Count == 0)
            {
                await EmitLog(ctx, $"[validate] AC review: ✓ satisfied. {review.Summary}");
            }
            else
            {
                var sb = new StringBuilder();
                sb.AppendLine($"[validate] AC review: ⚠ concerns raised (warning only — does not block Submit)");
                foreach (var c in review.Concerns) sb.AppendLine($"  - {c}");
                if (!string.IsNullOrEmpty(review.Summary)) sb.AppendLine($"  summary: {review.Summary}");
                await EmitLog(ctx, sb.ToString().TrimEnd());
            }
        }
        catch (NotSupportedException)
        {
            await EmitLog(ctx, "[validate] AC review skipped: agent does not support it.");
        }
        catch (Exception ex)
        {
            await EmitLog(ctx, $"[validate] AC review skipped: {ex.Message}");
        }
    }

    // ── Submit (commit + push + open PR) ──────────────────────────────────

    private async Task<StepRun> RunSubmitAsync(string ticketKey, StepRunContext ctx, CancellationToken ct)
    {
        await EmitLog(ctx, $"[orchestrator] preparing Submit run for {ticketKey}");

        var (workspace, _, ticket, allowedPaths) = await LoadRunStateAsync(ticketKey, requireApprovedPlan: false, ct).ConfigureAwait(false);

        // 1. Commit per repo.
        var (authorName, authorEmail) = ResolveCommitIdentity();
        foreach (var repoPath in allowedPaths)
        {
            var repoName = Path.GetFileName(repoPath);
            var uncommitted = await _git.GetUncommittedFilesAsync(repoPath, ct).ConfigureAwait(false);
            if (uncommitted.Count == 0)
            {
                await EmitLog(ctx, $"[commit] {repoName}: working tree clean — nothing to commit.");
                continue;
            }

            await EmitLog(ctx, $"[commit] {repoName}: staging {uncommitted.Count} file(s)");
            foreach (var path in uncommitted.Take(20))
                await EmitLog(ctx, $"  - {path}");
            if (uncommitted.Count > 20)
                await EmitLog(ctx, $"  …and {uncommitted.Count - 20} more");

            var message = BuildCommitMessage(ticket);
            var sha = await _git.CommitAllAsync(repoPath, message, authorName, authorEmail, ct).ConfigureAwait(false);
            if (sha is null)
                await EmitLog(ctx, $"[commit] {repoName}: nothing to commit after staging.");
            else
                await EmitLog(ctx, $"[commit] {repoName}: committed {sha[..8]}");
        }

        // 2. Delegate push + PR open to the existing WorkspaceManager.
        await EmitLog(ctx, "[submit] pushing branches and opening PRs…");
        IReadOnlyList<PullRequestResult> results;
        try
        {
            results = await _workspaces.SubmitAsync(ticketKey, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await EmitLog(ctx, $"[submit] failed: {ex.Message}");
            return new StepRun
            {
                Id = ctx.StepRunId,
                TicketKey = ticketKey,
                Step = PipelineStep.Submit,
                Status = StepRunStatus.Failed,
                AgentId = ctx.AgentId,
                StartedAt = DateTimeOffset.UtcNow,
                FinishedAt = DateTimeOffset.UtcNow,
                CancellationReason = ex.Message
            };
        }

        var allOpened = true;
        foreach (var r in results)
        {
            if (r.Url is not null)
                await EmitLog(ctx, $"[submit] {r.RepositoryName}: PR opened — {r.Url}");
            else
            {
                allOpened = false;
                await EmitLog(ctx, $"[submit] {r.RepositoryName}: failed — {r.Error}");
            }
        }

        return new StepRun
        {
            Id = ctx.StepRunId,
            TicketKey = ticketKey,
            Step = PipelineStep.Submit,
            Status = allOpened ? StepRunStatus.Succeeded : StepRunStatus.Failed,
            AgentId = ctx.AgentId,
            StartedAt = DateTimeOffset.UtcNow,
            FinishedAt = DateTimeOffset.UtcNow,
            SummaryJson = JsonSerializer.Serialize(results)
        };
    }

    private static string BuildCommitMessage(Ticket ticket)
    {
        var summary = string.IsNullOrWhiteSpace(ticket.Summary) ? ticket.Key : ticket.Summary;
        return $"{ticket.Key}: {summary}\n\n[lights-off autonomous]\n";
    }

    private static (string Name, string Email) ResolveCommitIdentity()
    {
        // Read from environment first, fall back to a sensible default. The user can override
        // by setting GIT_AUTHOR_NAME / GIT_AUTHOR_EMAIL before launching the app.
        var name = Environment.GetEnvironmentVariable("GIT_AUTHOR_NAME")
                   ?? Environment.GetEnvironmentVariable("USER")
                   ?? "lights-off";
        var email = Environment.GetEnvironmentVariable("GIT_AUTHOR_EMAIL")
                    ?? $"{Environment.UserName}@lights-off.local";
        return (name, email);
    }

    // ── Shared helpers ────────────────────────────────────────────────────

    private async Task<(Workspace Workspace, ImplementationPlan? Plan, Ticket Ticket, List<string> AllowedPaths)> LoadRunStateAsync(string ticketKey, bool requireApprovedPlan, CancellationToken ct)
    {
        var workspace = await _db.Workspaces
            .Include(w => w.Links)
            .FirstOrDefaultAsync(w => w.TicketKey == ticketKey, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No workspace for ticket {ticketKey}.");

        ImplementationPlan? plan = null;
        if (!string.IsNullOrEmpty(workspace.WorkflowStateJson))
        {
            var workflow = JsonSerializer.Deserialize<WorkflowState>(workspace.WorkflowStateJson, PersistOptions);
            plan = workflow?.Plan;
        }

        if (requireApprovedPlan)
        {
            if (plan is null)
                throw new InvalidOperationException("No plan on the workflow — run Plan first.");
            if (!plan.IsApproved)
                throw new InvalidOperationException("Plan is not approved; this step is locked.");
        }

        var ticket = new Ticket
        {
            Key = ticketKey,
            Summary = workspace.TicketSummary ?? ticketKey,
            Description = null,
            AcceptanceCriteria = null
        };

        var allowedPaths = new List<string>();
        foreach (var link in workspace.Links)
        {
            var repoPath = Path.Combine(_options.Value.ReposRoot, link.RepositoryName);
            if (Directory.Exists(repoPath))
                allowedPaths.Add(repoPath);
        }
        if (allowedPaths.Count == 0)
            throw new InvalidOperationException("No cloned repos found on host.");

        return (workspace, plan, ticket, allowedPaths);
    }

    private async Task<string> ResolveDefaultBranchAsync(Workspace workspace, string repoPath, CancellationToken ct)
    {
        var repoName = Path.GetFileName(repoPath);
        var repo = await _db.Repositories.FirstOrDefaultAsync(r => r.Name == repoName, ct).ConfigureAwait(false);
        return repo?.DefaultBranch ?? "main";
    }

    private async Task<(bool Success, string? Error)> PumpSessionAsync(IAgentSession session, StepRunContext ctx, CancellationToken ct)
    {
        using var cancelWatcher = ct.Register(() =>
        {
            try { _ = session.CancelAsync(CancellationToken.None); } catch { }
        });

        try
        {
            await foreach (var ev in session.Events.WithCancellation(ct).ConfigureAwait(false))
            {
                await ctx.EmitEvent(ev).ConfigureAwait(false);
                if (ev is SessionFinishedEvent sf) return (sf.Success, sf.Error);
            }
        }
        catch (OperationCanceledException)
        {
            return (false, "Cancelled");
        }
        return (false, "Session ended without a finish event.");
    }

    private static Task EmitLog(StepRunContext ctx, string text) =>
        ctx.EmitEvent(new LogChunkEvent(DateTimeOffset.UtcNow, "stdout", text + "\n"));

    private static string Tail(string s, int max) =>
        s.Length <= max ? s : "…" + s[^max..];

    private static string IndentLines(string s, string prefix) =>
        string.Join('\n', s.Split('\n').Select(l => prefix + l));

    private sealed record TestSummary(bool AllPassed, IReadOnlyList<TestFailure> Failures);
    private sealed record TestFailure(string ConfigName, string RepositoryName, string Command, int ExitCode, string OutputTail);
}
