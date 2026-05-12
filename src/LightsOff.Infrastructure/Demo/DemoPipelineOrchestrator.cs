using System.Text.Json;
using LightsOff.Core.Abstractions;
using LightsOff.Core.Domain;

namespace LightsOff.Infrastructure.Demo;

/// <summary>
/// Demo pipeline orchestrator: emits scripted events for Implement, Validate, and Submit. No
/// real git, docker, or PR calls happen — the UI sees a deterministic stream that matches what
/// the real orchestrator would produce on a successful run.
/// </summary>
internal sealed class DemoPipelineOrchestrator : IPipelineOrchestrator
{
    private static readonly TimeSpan ShortBeat = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan MediumBeat = TimeSpan.FromMilliseconds(320);

    public async Task<StepRun> RunStepAsync(string ticketKey, PipelineStep step, StepRunContext ctx, CancellationToken ct = default)
    {
        var startedAt = DateTimeOffset.UtcNow;

        try
        {
            switch (step)
            {
                case PipelineStep.Implement:
                    await RunImplementAsync(ticketKey, ctx, ct).ConfigureAwait(false);
                    break;
                case PipelineStep.Validate:
                    await RunValidateAsync(ticketKey, ctx, ct).ConfigureAwait(false);
                    break;
                case PipelineStep.Submit:
                    await RunSubmitAsync(ticketKey, ctx, ct).ConfigureAwait(false);
                    break;
                default:
                    throw new NotImplementedException(
                        $"Demo orchestrator does not handle step '{step}'. Analyze/Plan run via direct agent calls.");
            }

            // Final session marker so the UI's log viewer renders the green "session ended" footer.
            await ctx.EmitEvent(new SessionFinishedEvent(DateTimeOffset.UtcNow, Success: true, Error: null)).ConfigureAwait(false);

            return new StepRun
            {
                Id = ctx.StepRunId,
                TicketKey = ticketKey,
                Step = step,
                Status = StepRunStatus.Succeeded,
                AgentId = ctx.AgentId,
                StartedAt = startedAt,
                FinishedAt = DateTimeOffset.UtcNow,
            };
        }
        catch (OperationCanceledException)
        {
            await ctx.EmitEvent(new SessionFinishedEvent(DateTimeOffset.UtcNow, Success: false, Error: "Cancelled")).ConfigureAwait(false);
            return new StepRun
            {
                Id = ctx.StepRunId,
                TicketKey = ticketKey,
                Step = step,
                Status = StepRunStatus.Cancelled,
                AgentId = ctx.AgentId,
                StartedAt = startedAt,
                FinishedAt = DateTimeOffset.UtcNow,
                CancellationReason = "Cancelled",
            };
        }
    }

    // ── Implement ────────────────────────────────────────────────────────

    private static async Task RunImplementAsync(string ticketKey, StepRunContext ctx, CancellationToken ct)
    {
        await EmitLog(ctx, $"[orchestrator] preparing Implement run for {ticketKey} via agent '{ctx.AgentId}'");
        await Beat(ct, ShortBeat);
        await EmitLog(ctx, "[orchestrator] approved plan loaded — 4 task(s), 5 planned change(s)");
        await EmitLog(ctx, "[orchestrator] starting agent session…");
        await Beat(ct, MediumBeat);

        await EmitLog(ctx, "Reading the approved plan and ticket context…");
        await Beat(ct, ShortBeat);
        await EmitTool(ctx, "Read", new { path = "src/Api/Program.cs" });
        await Beat(ct, ShortBeat);
        await EmitLog(ctx, "Found middleware order — Routing is registered after Auth.");
        await EmitTool(ctx, "Read", new { path = "src/Api/appsettings.json" });
        await Beat(ct, ShortBeat);
        await EmitTool(ctx, "Write", new { path = "src/Api/Configuration/RateLimitOptions.cs" });
        await Beat(ct, ShortBeat);
        await EmitTool(ctx, "Write", new { path = "src/Api/Middleware/RateLimitMiddleware.cs" });
        await Beat(ct, ShortBeat);
        await EmitTool(ctx, "Edit", new { path = "src/Api/Program.cs" });
        await EmitTool(ctx, "Edit", new { path = "src/Api/appsettings.json" });
        await Beat(ct, ShortBeat);
        await EmitTool(ctx, "Write", new { path = "tests/Api.Tests/RateLimitTests.cs" });
        await Beat(ct, ShortBeat);
        await EmitTool(ctx, "Bash", new { command = "dotnet build" });
        await EmitLog(ctx, "Build succeeded.\n    0 Error(s)\n    0 Warning(s)");
        await Beat(ct, MediumBeat);
        await ctx.EmitEvent(new ChatReplyEvent(
            DateTimeOffset.UtcNow,
            "Implementation complete. Added `RateLimitMiddleware` (token-bucket, per-API-key) and `RateLimitOptions` bound to `RateLimit:RequestsPerMinute` (default 60). Registered before routing in `Program.cs`. Returns 429 + Retry-After when the bucket is empty. Tests cover happy path, exhausted bucket, and the header. 5 files changed.",
            IsFinal: true)).ConfigureAwait(false);
    }

    // ── Validate ─────────────────────────────────────────────────────────

    private static async Task RunValidateAsync(string ticketKey, StepRunContext ctx, CancellationToken ct)
    {
        await EmitLog(ctx, $"[orchestrator] preparing Validate run for {ticketKey}");
        await Beat(ct, ShortBeat);
        await EmitLog(ctx, "[validate] running 1 test config(s)…");
        await EmitTool(ctx, "Test", new
        {
            config = "unit+integration",
            repo = DemoFixtures.DemoRepo,
            command = "dotnet test --collect:\"XPlat Code Coverage\""
        });
        await Beat(ct, MediumBeat);
        await EmitLog(ctx, "Determining projects to restore...\nAll projects are up-to-date for restore.\n  checkout-api.Api -> bin/Debug/net8.0/checkout-api.Api.dll\n  checkout-api.Api.Tests -> bin/Debug/net8.0/checkout-api.Api.Tests.dll");
        await Beat(ct, ShortBeat);
        await EmitLog(ctx, "Test run for /workspace/checkout-api/tests/Api.Tests/bin/Debug/net8.0/checkout-api.Api.Tests.dll (.NETCoreApp,Version=v8.0)\nMicrosoft (R) Test Execution Command Line Tool Version 17.10.0");
        await Beat(ct, MediumBeat);
        await EmitLog(ctx, "Starting test execution, please wait...\nA total of 1 test files matched the specified pattern.");
        await Beat(ct, MediumBeat);
        await EmitLog(ctx, "Passed!  - Failed:     0, Passed:    26, Skipped:     0, Total:    26, Duration: 1 s - checkout-api.Api.Tests.dll");
        await Beat(ct, ShortBeat);
        await EmitLog(ctx, "[validate] PASS (unit+integration: dotnet test)");
        await Beat(ct, ShortBeat);
        await EmitLog(ctx, "[validate] tests: PASS\n[validate] coverage: 92.3% on new code (72/78 lines) — gate PASS");
        await Beat(ct, ShortBeat);
        await EmitLog(ctx, "[validate] AC review: ✓ satisfied. Diff introduces a per-API-key rate limit returning 429 + Retry-After, configurable via appsettings, with unit + integration tests — matches every acceptance bullet.");
    }

    // ── Submit ───────────────────────────────────────────────────────────

    private static async Task RunSubmitAsync(string ticketKey, StepRunContext ctx, CancellationToken ct)
    {
        await EmitLog(ctx, $"[orchestrator] preparing Submit run for {ticketKey}");
        await Beat(ct, ShortBeat);
        await EmitLog(ctx,
            $"[commit] {DemoFixtures.DemoRepo}: staging 5 file(s)\n" +
            "  - src/Api/Configuration/RateLimitOptions.cs\n" +
            "  - src/Api/Middleware/RateLimitMiddleware.cs\n" +
            "  - src/Api/Program.cs\n" +
            "  - src/Api/appsettings.json\n" +
            "  - tests/Api.Tests/RateLimitTests.cs");
        await Beat(ct, ShortBeat);
        await EmitLog(ctx, $"[commit] {DemoFixtures.DemoRepo}: committed a1b2c3d4");
        await Beat(ct, ShortBeat);
        await EmitLog(ctx, "[submit] pushing branches and opening PRs…");
        await Beat(ct, MediumBeat);
        await EmitLog(ctx, $"[submit] {DemoFixtures.DemoRepo}: PR opened — https://github.com/{DemoFixtures.DemoOwner}/{DemoFixtures.DemoRepo}/pull/42");
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static Task EmitLog(StepRunContext ctx, string text) =>
        ctx.EmitEvent(new LogChunkEvent(DateTimeOffset.UtcNow, "stdout", text + "\n"));

    private static Task EmitTool(StepRunContext ctx, string tool, object args) =>
        ctx.EmitEvent(new ToolCallEvent(DateTimeOffset.UtcNow, tool, JsonSerializer.Serialize(args)));

    private static async Task Beat(CancellationToken ct, TimeSpan span)
    {
        try { await Task.Delay(span, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
    }
}
