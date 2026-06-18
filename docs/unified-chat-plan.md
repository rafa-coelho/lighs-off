# Unified ticket chat — implementation plan

Goal (from the product owner): condense the whole ticket workflow into **one chat** where each
feature/step is invoked from **natural language** (no slash-commands). The agent infers intent and
drives the pipeline; progress for every step shows up inline in the same chat. Default harness:
**Claude Code (claude-cli)**. Remote access: expose the existing Blazor Server app over a tunnel.

This doc is the live checklist. The intent-router backbone (Phase 1a) has landed; the rest is staged
because the environment can't run a .NET 10 build (network policy blocks the SDK installer), so the
risky UI refactor is deferred to a session where `dotnet build` / `dotnet test` can verify it.

---

## Status

- [x] **Phase 1a — Intent router backbone** (Core + Infra + tests + DI). *Compiles by inspection;
      not yet build-verified.*
  - `src/LightsOff.Core/Domain/ChatIntent.cs` — `ChatIntent` enum + `IntentClassification` record
    (`Intent`, `Confidence`, `Rationale`, `Payload`, `Actionable`).
  - `src/LightsOff.Core/Abstractions/IIntentRouter.cs` — `ClassifyAsync(message, state, hasOpenQuestions, ct)`.
  - `src/LightsOff.Infrastructure/Chat/KeywordIntentRouter.cs` — deterministic PT+EN keyword router,
    gated by `WorkflowState` so it only marks reachable actions `Actionable`.
  - `tests/LightsOff.Tests/KeywordIntentRouterTests.cs` — 15+ cases.
  - DI: `services.AddScoped<IIntentRouter, KeywordIntentRouter>();` in `DependencyInjection.cs`.
- [ ] **Phase 1b — Wire the router into the unified chat** (`TicketDetail.razor`). *Risky; do with a build.*
- [ ] **Phase 1c — Render every step inline in the chat** (implement/validate/submit/review as cards).
- [ ] **Phase 2 — PR review loop** (read comments/findings, reply, address, resolve threads).
- [ ] **Phase 3 — Remote/mobile access** (bind, auth, responsive UI, tunnel docs).

---

## Phase 1b — wire the router into the chat

The single chat already exists for the Analyze+Plan phase: `TicketDetail.razor` `RenderPlanStep()`
(~line 767) renders one `ChatThread` over `_unifiedChatMessages` (~line 841, merges
`_analysis.Conversation` + `_plan.Comments` chronologically). The composer's send handler is
`OnPlanChatSendAsync` (~line 944).

Steps:

1. Inject the router: add `@inject IIntentRouter IntentRouter` at the top of `TicketDetail.razor`
   (alongside the other `@inject` lines, ~line 7-22).
2. Add a single entry point `OnChatSendAsync(string message)` that:
   a. builds the live `WorkflowState` (`_workflow`, already maintained) and `hasOpenQuestions`
      (`_activeQuestionsBatchId is not null`),
   b. calls `await IntentRouter.ClassifyAsync(message, _workflow, hasOpenQuestions)`,
   c. if `!result.Actionable`, append an agent `ChatMessage` with `result.Rationale` and return,
   d. otherwise `switch (result.Intent)` and dispatch to the existing methods:
      - `Analyze` → `RunAnalysisAsync()`
      - `RefinePlan` / `Chat` → existing `OnPlanChatSendAsync(message)` default path
      - `ReworkPlan` → `ReworkPlanWithFeedbackAsync(message)` (~line 989)
      - `ApprovePlan` → the Approve handler used by `PlanSidePanel` (`ApprovePlanFromPanelAsync`)
      - `AnswerQuestion` → feed into the active batch (`OnQuestionSubmit` / `TryFinalizeBatchAsync`)
      - `Implement` → `StepRunner.StartStep(TicketKey, PipelineStep.Implement, agentId)` (~line 2383)
      - `RunTests` → the Validate trigger (`StepRunner.StartStep(..., Validate, ...)`)
      - `Submit` → the Submit trigger (`StepRunner.StartStep(..., Submit, ...)`)
      - `RunAutonomous` → `StartAutonomousChainAsync()` (~line 143 CTA handler)
      - `Cancel` → `CancelImplementRunAsync()` (~line 136)
      - `ReviewPr` / `AddressPrFindings` → Phase 2 handlers (below)
   e. before dispatching a step, append a narrative agent message ("Implementing now…", "Running
      tests…", "Opening the PR…") so the chat always says something (matches the redesign brief in
      `docs/chat-redesign-handoff.md`).
3. Point `ChatThread.OnSend` at `OnChatSendAsync` instead of the plan-only handler. Keep the chat
   visible regardless of `_activeStep` (today the chat only renders in the Plan view) — that's what
   makes it the single surface. The `PipelineStepper` (~line 152) becomes a status indicator, not the
   primary navigation.

Confidence policy: act when `Confidence >= 0.6 && Actionable`. Below that, post the rationale and ask
for confirmation. (Threshold is cheap to tune once the LLM router exists.)

Optional follow-up: an `LlmIntentRouter` that calls the Claude one-shot for fuzzy phrasing and falls
back to `KeywordIntentRouter`. Reuse `ClaudeCliOneShot` (see `ClaudeCliAgent.OneShot`). Same interface.

---

## Phase 1c — render steps inline

Today Implement/Validate/Submit are separate render methods (`RenderImplementStep`, etc.) reading the
`StepRunner` event stream via `StepLogViewer`. To unify:

- Extend `ChatAttachment` (in `TicketAnalysis.cs`) with new kinds: `KindRun` (live step log),
  `KindTestReport`, `KindPr`, `KindReview`.
- When a step starts, append an agent `ChatMessage` carrying a `KindRun` attachment whose payload is
  the `StepRunId`; render it with an inline, collapsible `StepLogViewer` inside `ChatThread`.
- On completion, append a result card: `KindTestReport` (pass/fail + coverage), `KindPr` (links), or
  `KindReview` (findings). Reuse the existing rendering from the step views.
- The `OnStepRunUpdated` subscription (~line 271) already drives live refresh; just make it re-render
  the chat instead of the step section.

---

## Phase 2 — PR review loop

`PipelineStep.Review` exists but is unused. `IGitHubClient`/`GitHubClient` only do
`CreatePullRequestAsync` + `FindPullRequestForBranchAsync`. Add:

- `IGitHubClient.ListReviewThreadsAsync(owner, repo, prNumber)` → threads with: id, file, line,
  isResolved, and comments (author, body, id). Use the **GraphQL** API
  (`repository.pullRequest.reviewThreads`) — REST can't express resolved state.
- `IGitHubClient.ReplyToReviewCommentAsync(...)` → REST
  `POST /repos/{o}/{r}/pulls/{n}/comments/{id}/replies`.
- `IGitHubClient.ResolveReviewThreadAsync(threadId)` → GraphQL `resolveReviewThread` mutation
  (and `unresolveReviewThread` for symmetry).
- A `PrReviewFindings` domain type + a `ReviewPr` orchestrator path that fetches threads and posts a
  `KindReview` card to the chat summarizing them.
- `AddressPrFindings`: feed unresolved threads into a Claude session (Implement-mode) to change code
  and/or draft replies; then reply + resolve via the new client methods. Surface each action in chat.

GitHub PAT needs **Pull requests: Read/Write** (already documented in README's token cheat sheet).

---

## Phase 3 — remote/mobile access

Blazor Server is already a website, so the fast path is exposure, not a new client:

- Bind beyond localhost: make Kestrel URL configurable (env/appsettings), e.g. `http://0.0.0.0:5186`
  (`appsettings.json` Kestrel section + `Program.cs`).
- Add lightweight auth (single-user token/PIN middleware) since it's no longer localhost-only.
- Make the UI responsive (the chat layout is `max-w-3xl` already; audit nav + step views for mobile).
- Document a tunnel (Tailscale / Cloudflare Tunnel / ngrok) in the README; recommend Tailscale for a
  private, always-on path from the phone. SignalR (Blazor Server's transport) already gives realtime
  updates over the tunnel — no extra streaming endpoint needed.

---

## Build note

This environment lacks the .NET 10 SDK and the network policy must allow
`builds.dotnet.microsoft.com`, `dotnetcli.azureedge.net`, `dotnetcli.blob.core.windows.net`,
`aka.ms`, and `*.nuget.org` to install it. Egress changes apply to **new** sessions. A setup script
that runs `dotnet-install.sh --channel 10.0` caches the SDK for subsequent sessions. Until then,
verify with `dotnet build lighs-off.slnx` + `dotnet test tests/LightsOff.Tests` locally before
relying on the staged phases above.
