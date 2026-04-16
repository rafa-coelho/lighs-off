# lights-off

Jira-driven multi-repo workspace orchestrator. Pick a ticket, pick one or more repos, get a pre-branched, pre-mounted Docker dev container — and push everything back as PRs when you're done.

All-in-one .NET solution: Blazor Server UI, REST API, `BackgroundService` workers, and a CLI sharing one Core library.

## Requirements

- **.NET 10 SDK**
- **Docker Desktop** (running) — for per-ticket containers
- **GitHub PAT** (fine-grained recommended) with `Metadata (read)` + `Contents (read)`; add `Pull requests (write)` if you'll use Submit PRs
- **Jira Cloud API token** — any Atlassian account with board access

## Quickstart

```sh
# From the repo root
dotnet run --project src/LightsOff.App
```

Open [http://localhost:5186](http://localhost:5186). You'll land on the Home page. Follow the 4-step setup:

1. **[/settings](http://localhost:5186/settings)** — paste Jira base URL + email + API token; paste GitHub PAT. Click **Test & save**.
2. **[/boards](http://localhost:5186/boards)** — click **Set active** on the board you care about.
3. **[/tickets](http://localhost:5186/tickets)** — pick a ticket.
4. On the ticket page, check the repos you want to work on, confirm the branch name, click **Create workspace**.

The app clones each repo, fetches the default branch, creates your feature branch, and spins up a Docker container with each repo bind-mounted at `/workspace/<repo>`. Copy the `docker exec -it …` command and you're in.

When you're done, **Submit PRs** pushes every linked repo's feature branch and opens a pull request against the default branch. Results show up inline with links.

## CLI

The CLI talks to the running App over HTTP at `http://localhost:5186` (override with `LIGHTSOFF_API` env var or `--api`).

```sh
dotnet run --project src/LightsOff.Cli -- --help
```

**Headless setup** (so you don't have to open Settings):

```sh
lightsoff settings jira -u https://you.atlassian.net -e you@example.com -t <jira-token>
lightsoff settings github --token ghp_...
```

**Everyday flow:**

```sh
lightsoff boards list
lightsoff tickets list --board 42
lightsoff workspace create SCRUM-1 --repo my-app --repo my-api --summary "Fix login"
$(lightsoff workspace attach SCRUM-1)   # exec into the container immediately
lightsoff workspace submit SCRUM-1       # push branches + open PRs
lightsoff workspace destroy SCRUM-1
```

**Full command surface:**

| Group | Subcommand | What it does |
|---|---|---|
| `boards` | `list` | List Jira boards |
| `tickets` | `list --board <id> [--jql …]` | List tickets on a board |
| `repos` | `list` / `add <name> --url … --branch …` / `update` / `remove` | Manage the repo registry |
| `workspace` | `list` / `create <KEY> --repo … --summary …` / `start` / `stop` / `attach` / `submit` / `destroy` | Full lifecycle |
| `settings` | `jira …` / `github …` | Headless credential setup |

## `.lightsoff.yaml` (optional per-repo config)

Drop a `.lightsoff.yaml` at the root of any registered repo to customize the container for that repo:

```yaml
image: node:20-alpine
ports:
  - "3000:3000"
  - "127.0.0.1:5432:5432"
env:
  NODE_ENV: development
  DEBUG: "app:*"
post_create_commands:
  - npm install
  - npm run db:migrate
```

- **image** — overrides the global default. If multiple linked repos disagree, the first one wins (logged).
- **ports** — host-to-container TCP mappings. Accepts `"N"`, `"HOST:CONTAINER"`, or `"IP:HOST:CONTAINER"`.
- **env** — env vars injected into the container.
- **post_create_commands** — shell commands run inside the container after start, `cd`'d into `/workspace/<repo>`. Failures are logged but don't abort creation.

## State layout

Everything lives under `~/.lightsoff/`:

```
~/.lightsoff/
├── state.db          # SQLite: repo registry, workspaces, settings
├── secrets.json      # Jira + GitHub tokens (chmod 0600)
├── repos/            # Clones, one directory per registered repo
├── workspaces/       # Per-ticket directories with symlinks to the above
└── logs/             # Daily-rolling Serilog output
```

None of it is inside the project — the App only writes to your home.

## Endpoints (API surface)

```
GET    /api/health                           health of db/docker/jira/github
GET    /api/jira/connection                  current Jira config (no token)
POST   /api/jira/connection                  { baseUrl, email, apiToken } — tests + persists
GET    /api/jira/active-board                active board id
PUT    /api/jira/active-board                { boardId }
POST   /api/github/pat                       { token } or { token: null } to clear
GET    /api/github/pat/status                configured?
GET    /api/github/repos                     search is:private, token-scoped
GET    /api/boards
GET    /api/tickets?boardId=…&jql=…
GET    /api/tickets/{key}
GET    /api/repos
POST   /api/repos
PUT    /api/repos/{name}
DELETE /api/repos/{name}
GET    /api/workspaces
GET    /api/workspaces/{key}
POST   /api/workspaces                       { ticketKey, ticketSummary, repositoryNames[], branchNameOverride?, startContainer }
POST   /api/workspaces/{key}/start
POST   /api/workspaces/{key}/stop
POST   /api/workspaces/{key}/submit          pushes branches, opens PRs
DELETE /api/workspaces/{key}
```

Errors from the API group are always `application/problem+json` with a clean `detail` field; the CLI parses and prints one-line messages like `lights-off: not found: Workspace SCRUM-1 not found.`

## Token permission cheat sheet (GitHub fine-grained PAT)

Scope the token to the org you want to work in. Grant:

| Permission | Access | Why |
|---|---|---|
| Repository / Metadata | Read | mandatory for any repo API |
| Repository / Contents | Read | clone + read |
| Repository / Contents | **Write** | required if you use `workspace submit` |
| Repository / Pull requests | Read/Write | required for PR creation |

If you want the Settings "Organizations to browse" dropdown to list more than the orgs derived from your accessible private repos, also grant **Organization / Members: Read**.

## Known limits

- **GitHub listing APIs don't hide public repos regardless of token scope.** `is:private` search avoids this; picking an org in Settings calls `/orgs/{org}/repos` which is also strictly scoped.
- **Jira Cloud only** (REST v3 + Basic auth with email:token). Server/Data Center would need bearer-token support.
- **GitHub only** for Git hosting. GitLab/Bitbucket would need their own `IGit*Client`.
- **No multi-user/auth** — personal local tool, one user, binds to `http://localhost:5186`.

## Project layout

```
lighs-off.slnx
├── src/
│   ├── LightsOff.Core/            # Domain + interfaces (no infra deps)
│   ├── LightsOff.Infrastructure/  # Jira, GitHub, Git (LibGit2Sharp), Docker, EF/SQLite
│   ├── LightsOff.App/             # Blazor Server + minimal API + hosted workers
│   └── LightsOff.Cli/             # System.CommandLine, HTTP against the App
└── tests/LightsOff.Tests/
```

## Troubleshooting

- **CLI hits 404 / connection refused** — App isn't running. Start it, or override `--api http://other:port`.
- **"No organization repositories visible to this PAT"** — your token lacks `Metadata + Contents (read)` on the org's repos, or SSO isn't authorized. Go to [github.com/settings/tokens](https://github.com/settings/tokens) and fix.
- **Repo not visible inside the container** — an old workspace from before the direct-mount fix. Destroy and recreate.
- **`workspace submit` fails with 403** — PAT is missing `Pull requests: Write` or `Contents: Write`.
- **Lost Jira config after restart** — `~/.lightsoff/state.db` got wiped. Tokens in `secrets.json` usually survive; re-enter URL + email in Settings, leave token blank.

## Development

```sh
dotnet build lighs-off.slnx          # full solution
dotnet test tests/LightsOff.Tests    # xUnit suite
dotnet run --project src/LightsOff.App
```

Implementation history and phase breakdown: [docs/implementation-plan.md](docs/implementation-plan.md).
