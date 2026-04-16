# lights-off — Implementation plan

Living document. Each phase has a status marker that gets flipped from `[ ]` to `[x]` as work lands. When a phase's tasks are done, update the **Status** line and check off the items below it.

---

## Architecture snapshot

All-in-one .NET solution targeting `net10.0`:

```
lighs-off.slnx
├── src/
│   ├── LightsOff.Core/            # Domain + interfaces (no infra deps)
│   ├── LightsOff.Infrastructure/  # Jira, Git, Docker, EF Core/SQLite, secrets
│   ├── LightsOff.App/             # Blazor Server + minimal API + BackgroundServices
│   └── LightsOff.Cli/             # System.CommandLine, talks to App REST API
└── tests/
    └── LightsOff.Tests/
```

Runtime state: `~/.lightsoff/` — `state.db` (SQLite), `secrets.json` (0600), `repos/`, `workspaces/`.

Default dev URL: `http://localhost:5186` (pinned in `appsettings.json` Kestrel section).

---

## Phase 0 — Scaffolding

**Status:** Done.

- [x] Solution (`lighs-off.slnx`) + 5 projects, all building clean
- [x] Local `NuGet.config` scoping to nuget.org (sidesteps private CodeArtifact feed)
- [x] Core domain: `Board`, `Ticket`, `Repository`, `TicketRepositoryLink`, `Workspace`, `JiraConnection`
- [x] Core abstractions: `IJiraClient`, `IGitService`, `IDockerService`, `IWorkspaceManager`, `IRepositoryRegistry`, `ISecretStore`, `IJiraConnectionProvider`
- [x] Core utilities: `TextSlug` (branch-name slugification, unicode-aware)
- [x] Core configuration: `LightsOffOptions`
- [x] Infrastructure: EF Core SQLite `AppDbContext`, `JiraClient` (HttpClient), `LibGit2Service`, `DockerService` (Docker.DotNet), `FileSecretStore`, `WorkspaceManager`, `RepositoryRegistry`, `JiraConnectionProvider`
- [x] App: Blazor Server + MudBlazor dark theme, placeholder pages (Home, Settings, Boards, Tickets, Repos, Workspaces)
- [x] App: Minimal API at `/api/*` — jira connection, boards, tickets, repos, workspaces
- [x] App: `JiraSyncWorker` + `WorkspaceHealthWorker` (`BackgroundService`)
- [x] CLI: `boards list`, `tickets list`, `repos list`, `workspace list|create|destroy` over REST
- [x] Tests: xUnit with 5 passing slug tests
- [x] Pin App Kestrel port to 5186 in `appsettings.json` + CLI default matches

---

## Phase 1 — Jira connection + read-only board/ticket browsing

**Status:** Code complete, awaiting live Jira verification.

Goal: end-to-end read path — configure Jira once, list boards, list tickets.

- [x] Added `GET/PUT /api/jira/active-board` + `POST /api/github/pat` + `GET /api/github/pat/status` endpoints
- [x] `Settings.razor` — two-card form (Jira connection + GitHub PAT), calls `IJiraClient.TestConnectionAsync` then persists via `IJiraConnectionProvider.SaveAsync`; snackbar feedback; preserves stored token when field left blank
- [x] `Boards.razor` — lists via `IJiraClient.GetBoardsAsync`, "Set active" button, "Tickets" shortcut, "Jira not configured" alert with link to `/settings`
- [x] `Tickets.razor` — fetches for active board (or `?boardId=N` from query), JQL filter input sent to Jira, client-side status dropdown, row-click navigates to `/tickets/{key}`
- [x] `TicketDetail.razor` — read-only ticket detail with breadcrumb + "Open in Jira" link; placeholder for Phase 3 "Create workspace"
- [x] Error UX: `MudAlert` on not-configured / no-active-board / fetch errors
- [x] Smoke: bad credentials → `/api/jira/connection` correctly returns 400 `{"error":"Could not authenticate against Jira."}`
- [ ] **Pending live verification**: configure with real Atlassian credentials in the browser, confirm boards/tickets render and `Set active` persists. Same via CLI: `dotnet run --project src/LightsOff.Cli -- boards list`.

**Acceptance:** (after live verification) from a fresh clone, `dotnet run --project src/LightsOff.App`, configure Jira via UI, see boards and tickets. Same via `dotnet run --project src/LightsOff.Cli -- boards list`.

---

## Phase 2 — Repository registry UI

**Status:** Done.

Goal: manage the list of repos the tool knows about.

- [x] `PUT /api/repos/{name}` endpoint (new) + existing `POST`, `DELETE`, `GET`
- [x] Reusable `Modal.razor` component (Tailwind, headless, `@bind-Open`, ESC + backdrop close, size presets)
- [x] `Repos.razor` — add/edit/delete via modal dialogs; fields: name (immutable on edit), clone URL, default branch, optional container image
- [x] Inline validation: required fields, name-without-spaces, URL prefix check (https/http/ssh/git@)
- [x] Delete confirmation modal noting that on-disk clones aren't removed
- [x] CLI: `lightsoff repos add <name> --url … --branch … [--image …]`, `repos update <name> [--url] [--branch] [--image]` (preserves unchanged fields), `repos remove <name>`
- [x] Clean 404 when updating a non-existent repo: `lights-off: not found: Repository 'ghost' not found.`
- [x] **Browse from GitHub** flow — `IGitHubClient` + `GitHubClient` in Infrastructure, `GET /api/github/repos` endpoint, `GitHubBrowseModal.razor` with search + HTTPS/SSH toggle + multi-select + owner filter chips + "already registered" badges. Repos page gets primary "Browse GitHub" + secondary "Add manually" buttons.
- [x] **Discovery scope: two modes** depending on whether the user picks organizations in Settings.
  - **No orgs picked (default)** — `GET /search/repositories?q=is:private`. GitHub's Search API strictly respects fine-grained PAT scope for private repos (verified: a PAT scoped to 3 NyxLab-Dev repos returns exactly those 3 — zero leak from other orgs the user happens to belong to on GitHub).
  - **One or more orgs picked** — `GET /orgs/{org}/repos` per pinned org in parallel, combined and de-duped. Includes both private and public repos the token can see within those orgs. Per-org failures are logged and skipped so one bad org doesn't kill the list.
  - The Settings GitHub card has a multi-select checklist populated from `/user/orgs`. PAT fine-grained tokens with "Organization Members" permission see their orgs; tokens without it fall cleanly to the search-only path.
- [x] **JiraSyncWorker removed** — was a log-only no-op, deleted along with its `AddHostedService` registration. Will reintroduce with a real implementation when caching becomes necessary.

**Acceptance:** verified end-to-end — CLI add → list → update branch → remove all work; `/api/github/repos` returns the real user repo list (verified 17 repos from live GitHub API using the stored PAT).

---

## Phase 3 — Workspace creation (manual Docker)

**Status:** Code complete, awaiting live verification against a real ticket + repo.

Goal: the headline feature — pick a ticket, link repos, clone + branch.

- [x] `TicketDetail.razor` — **inline GitHub repo search** (via `IGitHubClient.ListAccessibleReposAsync`, same source as the Browse modal) with filter input + checkbox list, editable branch name (prefilled via `TextSlug.BuildBranchName` from `LightsOffOptions.BranchNameTemplate`), Create-workspace button. No pre-registration step — selected repos are auto-added to the registry on create, keeping CLI compatibility without making the user touch `/repos` first.
- [x] Calls `IWorkspaceManager.CreateAsync` directly (same backing logic as `POST /api/workspaces` used by CLI) with `CreateWorkspaceRequest { TicketKey, TicketSummary, RepositoryNames, BranchNameOverride, StartContainer }`
- [x] `StartContainer` auto-set from `IDockerService.IsDockerAvailableAsync()` — if Docker is down, create still proceeds without a container and the UI shows an amber alert
- [x] GitHub PAT — already stored via Settings page in earlier phase
- [x] Post-create view: shows workspace status badge, root path, container name, and a **copyable `docker exec -it <name> bash`** via `navigator.clipboard.writeText`. Destroy button removes the container + workspace row.
- [x] Guards: if a workspace already exists for the ticket, the "create" form is replaced with the existing-workspace view; "No repositories registered" empty-state links to `/repos`.

**Acceptance (to verify in browser):** pick a real Jira ticket → check one registered repo → edit branch name if needed → **Create workspace** → confirm `~/.lightsoff/repos/{repo}/` exists on branch `feature/{KEY}-{slug}`, container `lightsoff-{key}` shows in `docker ps`, and the UI's attach command actually exec's into it.

---

## Phase 4 — Docker dev container per ticket

**Status:** Done.

Goal: spawn a container per workspace, attach workflow.

- [x] `DockerService` pulls + creates + starts containers on Docker Desktop (verified on macOS)
- [x] **Multi-bind fix**: `IDockerService.CreateWorkspaceContainerAsync` now takes a list of `WorkspaceBind(HostPath, ContainerPath)` and `WorkspaceManager` mounts each repo directly at `/workspace/{name}`. Previous single-bind of the workspace root with symlinks *inside* didn't resolve inside the container — this is fixed.
- [x] `Workspaces.razor` — per-row actions: **Copy attach command** (via `navigator.clipboard.writeText`), **Start** / **Stop** (status-aware), **Destroy**. Busy spinner per row prevents double-clicks. Manual Refresh button in the header. Alert banner when Docker daemon is unreachable.
- [x] `IWorkspaceManager.StartAsync(ticketKey)` + `POST /api/workspaces/{key}/start` — resumes a stopped container. Throws a clear error if the workspace has no container (was created with Docker offline).
- [x] Multi-image warning: when repos linked to a ticket disagree on their container `Image` override, `WorkspaceManager` logs a warning and uses the first repo's image.
- [ ] (Deferred) `.lightsoff.yaml` per-repo file override for image/command — skip until a concrete need appears.

**Acceptance:** `docker ps` shows `lightsoff-<key>` container; `docker exec -it <name> ls /workspace/<repo>` shows the repo files (now directly bind-mounted, not symlinked). Start/Stop/Destroy buttons on `/workspaces` exercise the API correctly.

---

## Phase 5 — CLI parity and polish

**Status:** Done.

- [x] `workspace list|create|destroy` — since Phase 0
- [x] `lightsoff workspace start|stop <KEY>` — lifecycle actions
- [x] `lightsoff workspace attach <KEY>` — prints `docker exec -it <name> bash` to stdout so you can wrap it in `$(…)` for one-shot attach
- [x] `lightsoff repos list|add|update|remove` — landed in Phase 2
- [x] `lightsoff settings jira --base-url … --email … --token …` — headless Jira setup, POSTs `/api/jira/connection` which tests + persists
- [x] `lightsoff settings github --token ghp_… | --clear` — headless GitHub PAT management
- [x] Non-zero exit codes with clean one-line error messages (`lights-off: not configured: …`, `lights-off: not found: …`, etc.)
- [x] API endpoints `Results.NotFound()` replaced with thrown `KeyNotFoundException` so the minimal-API filter emits proper `ProblemDetails` JSON — CLI error output is no longer HTML from the Blazor 404 page
- [ ] (Deferred) Tab-completion — `dotnet-suggest` integration; skip until someone asks for it

---

## Phase 6 — Background sync + observability

**Status:** Done (observability scope; cache + toast push deferred).

- [x] **Serilog** — console + daily-rolling file sink at `~/.lightsoff/logs/app-<date>.log`, 14-day retention, structured template with `SourceContext`. `UseSerilogRequestLogging()` in the pipeline for request traces. Fatal crashes hit the file before the process dies.
- [x] **`GET /api/health`** — runs four checks in parallel with a 3s global timeout:
  - `database` — `CanConnectAsync` against SQLite
  - `docker` — `IDockerService.IsDockerAvailableAsync`
  - `jira` — if configured, live-tests against the Atlassian site via `IJiraClient.TestConnectionAsync`
  - `github` — reports `configured` / `not-configured` from `ISecretStore`
  - Returns `status: healthy | degraded` and `version`, plus each check's detail.
- [ ] (Deferred) `JiraSyncWorker` caching — premature. Pages fetch Jira live and it's fast enough; add caching the day someone complains. Keeps the "no stubs" principle intact.
- [ ] (Deferred) SignalR toast on container-died — the existing `WorkspaceHealthWorker` updates DB status every minute, and refreshing `/workspaces` reflects it. Push notification is a polish win, not essential.

---

## UI stack

- **Tailwind v4** via `unpkg.com/@tailwindcss/browser@4` CDN in [App.razor](../src/LightsOff.App/Components/App.razor). Zero-config for a local dev tool. Migrate to a proper `tailwindcss` CLI build step with a generated `app.css` before packaging for distribution.
- **Dark by default** (`<html class="dark">`, `color-scheme: dark`). Neutral `zinc` palette with `emerald-400` accent.
- **No UI framework** — MudBlazor was removed. Small internal kit under `src/LightsOff.App/Components/Shared/`:
  - `Icon.razor` — inline Heroicons-style SVG dictionary
  - `Spinner.razor`, `Alert.razor`, `EmptyState.razor`, `PageHeader.razor` — thin reusable primitives
  - `ToastHost.razor` + `Services/ToastService.cs` — scoped toast bus (no JSInterop needed)
- Pages talk directly to Core interfaces (`IJiraClient`, `IJiraConnectionProvider`, `IRepositoryRegistry`, `IWorkspaceManager`, `ISecretStore`, `ToastService`) via `@inject`; no REST round-trip from the UI.

## Known gotchas / notes

- **Port 5000 on macOS**: other apps frequently claim it (AirPlay Receiver, some local web tools). Our App binds `http://localhost:5186` via Kestrel config in `appsettings.json` to avoid collisions.
- **Private NuGet feed**: global config referenced an AWS CodeArtifact feed that 401'd. The local `NuGet.config` clears sources and uses only nuget.org. If needed for other projects, restore at the user level.
- **LibGit2Sharp on Apple Silicon**: version 0.31.0 bundles `osx-arm64` native binaries; verified builds. If `dlopen` errors appear at runtime, bump to next patch release.
- **Secrets storage**: plain JSON at `~/.lightsoff/secrets.json` with Unix 0600 perms. Good enough for a single-user local tool. Upgrade to macOS Keychain later if we ever share this.
- **Tailwind CDN flash**: the browser compiles Tailwind JIT on first load, which produces a brief unstyled flash. Acceptable for dev, needs a build step for production.

---

## Out of scope (explicitly deferred)

- Multi-user auth / hosted deployment
- GitLab / Bitbucket support
- `devcontainer.json` generation for VS Code
- Auto-PR creation on branch push
- Team-shared state synchronization
