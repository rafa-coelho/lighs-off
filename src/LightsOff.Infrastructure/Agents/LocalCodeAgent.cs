using System.Text;
using System.Text.RegularExpressions;
using LightsOff.Core.Abstractions;
using LightsOff.Core.Domain;
using Microsoft.Extensions.Logging;

namespace LightsOff.Infrastructure.Agents;

public partial class LocalCodeAgent : ICodeAgent
{
    private readonly ILogger<LocalCodeAgent> _logger;

    public LocalCodeAgent(ILogger<LocalCodeAgent> logger) => _logger = logger;

    public string Id => "local";
    public string DisplayName => "Local (file-system + rules)";
    public bool RequiresApiKey => false;
    public bool IsAvailable => true;

    // ── Repo analysis ──────────────────────────────────────────────────────

    public Task<RepoContext> AnalyzeRepositoryAsync(
        string repoPath,
        RepoContext? existingContext = null,
        CancellationToken ct = default)
    {
        var repoName = Path.GetFileName(repoPath);
        var stack = TechStackDetector.Detect(repoPath);

        var ctx = new RepoContext
        {
            RepoName = repoName,
            AgentUsed = Id,
            TechStack = TechStackDetector.FormatStack(stack),
            DirectoryTree = BuildDirectoryTree(repoPath, maxDepth: 3),
            EntryPoints = FindEntryPoints(repoPath, stack),
            ConfigFiles = FindConfigFiles(repoPath),
            ApiSurface = FindApiEndpoints(repoPath, stack),
            DataModel = FindDataEntities(repoPath, stack),
            TestInfo = DetectTestSetup(repoPath, stack),
            TopDependencies = ParseDependencies(repoPath, stack),
            RecentHistory = GetRecentCommits(repoPath),
            ReadmeSummary = ReadReadmeSummary(repoPath)
        };

        RepoContextSerializer.Save(repoPath, ctx);
        _logger.LogInformation("Analyzed {Repo}: {Stack}, {Endpoints} endpoints, {Entities} entities",
            repoName, ctx.TechStack, ctx.ApiSurface.Count, ctx.DataModel.Count);

        return Task.FromResult(ctx);
    }

    // ── Dependency detection ─────────────────────────────────────────────

    public Task<RepoDependencyProfile> DetectDependenciesAsync(string repoPath, CancellationToken ct = default)
    {
        var repoName = Path.GetFileName(repoPath);
        var runtimes = new List<RuntimeRequirement>();
        var pkgCmds = new List<string>();

        // Scan repo root + 2 levels deep for mono-repo support
        var dirsToScan = new List<(string Path, string? SubPath)> { (repoPath, null) };
        try
        {
            foreach (var sub in Directory.EnumerateDirectories(repoPath)
                .Where(d => !ShouldSkipDir(Path.GetFileName(d))))
            {
                var subName = Path.GetFileName(sub);
                dirsToScan.Add((sub, subName));
                foreach (var sub2 in Directory.EnumerateDirectories(sub)
                    .Where(d => !ShouldSkipDir(Path.GetFileName(d))).Take(10))
                {
                    dirsToScan.Add((sub2, $"{subName}/{Path.GetFileName(sub2)}"));
                }
            }
        }
        catch { /* permission errors, etc. */ }

        foreach (var (dir, subPath) in dirsToScan)
        {
            // Node.js
            if (File.Exists(Path.Combine(dir, "package.json")))
            {
                var version = DetectNodeVersion(dir);
                runtimes.Add(new RuntimeRequirement("node", version, subPath));

                var installCmd = DetectNodeInstallCommand(dir);
                var prefix = subPath is null ? "" : $"cd /workspace/{repoName}/{subPath} && ";
                pkgCmds.Add($"{prefix}{installCmd}");
            }

            // .NET
            var csprojFiles = Directory.EnumerateFiles(dir, "*.csproj", SearchOption.TopDirectoryOnly).ToList();
            if (csprojFiles.Count > 0 || File.Exists(Path.Combine(dir, "*.sln")))
            {
                var version = DetectDotnetVersion(dir);
                runtimes.Add(new RuntimeRequirement("dotnet", version, subPath));
                var prefix = subPath is null ? "" : $"cd /workspace/{repoName}/{subPath} && ";
                pkgCmds.Add($"{prefix}dotnet restore");
            }

            // Python
            if (File.Exists(Path.Combine(dir, "requirements.txt")) ||
                File.Exists(Path.Combine(dir, "pyproject.toml")) ||
                File.Exists(Path.Combine(dir, "setup.py")))
            {
                var version = DetectPythonVersion(dir);
                runtimes.Add(new RuntimeRequirement("python", version, subPath));
                if (File.Exists(Path.Combine(dir, "requirements.txt")))
                {
                    var prefix = subPath is null ? "" : $"cd /workspace/{repoName}/{subPath} && ";
                    pkgCmds.Add($"{prefix}pip install -r requirements.txt");
                }
            }

            // Go
            if (File.Exists(Path.Combine(dir, "go.mod")))
            {
                var version = DetectGoVersion(dir);
                runtimes.Add(new RuntimeRequirement("go", version, subPath));
                var prefix = subPath is null ? "" : $"cd /workspace/{repoName}/{subPath} && ";
                pkgCmds.Add($"{prefix}go mod download");
            }

            // Java
            if (File.Exists(Path.Combine(dir, "pom.xml")) ||
                File.Exists(Path.Combine(dir, "build.gradle")) ||
                File.Exists(Path.Combine(dir, "build.gradle.kts")))
            {
                runtimes.Add(new RuntimeRequirement("java", "21", subPath));
            }

            // Ruby
            if (File.Exists(Path.Combine(dir, "Gemfile")))
            {
                var version = DetectRubyVersion(dir);
                runtimes.Add(new RuntimeRequirement("ruby", version, subPath));
                var prefix = subPath is null ? "" : $"cd /workspace/{repoName}/{subPath} && ";
                pkgCmds.Add($"{prefix}bundle install");
            }

            // Rust
            if (File.Exists(Path.Combine(dir, "Cargo.toml")))
            {
                runtimes.Add(new RuntimeRequirement("rust", "latest", subPath));
                var prefix = subPath is null ? "" : $"cd /workspace/{repoName}/{subPath} && ";
                pkgCmds.Add($"{prefix}cargo fetch");
            }
        }

        // Deduplicate runtimes by type (keep first = shallowest path)
        var deduped = runtimes
            .GroupBy(r => r.Runtime, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        var profile = new RepoDependencyProfile
        {
            RepositoryName = repoName,
            AgentUsed = Id,
            Runtimes = deduped,
            PackageInstallCommands = pkgCmds
        };

        _logger.LogInformation("Detected dependencies for {Repo}: {Runtimes}",
            repoName, string.Join(", ", deduped.Select(r => $"{r.Runtime}:{r.Version}")));

        return Task.FromResult(profile);
    }

    private static bool ShouldSkipDir(string name) =>
        name is "node_modules" or "bin" or "obj" or ".git" or "dist" or ".next" or
            "__pycache__" or "venv" or ".venv" or "vendor" or "target" or ".lightsoff" or
            ".vs" or ".idea" or "coverage" or ".cache";

    private static string DetectNodeVersion(string dir)
    {
        // .nvmrc
        var nvmrc = Path.Combine(dir, ".nvmrc");
        if (File.Exists(nvmrc))
        {
            var v = File.ReadAllText(nvmrc).Trim().TrimStart('v');
            if (int.TryParse(v.Split('.')[0], out var major) && major > 0) return major.ToString();
        }
        // .node-version
        var nv = Path.Combine(dir, ".node-version");
        if (File.Exists(nv))
        {
            var v = File.ReadAllText(nv).Trim().TrimStart('v');
            if (int.TryParse(v.Split('.')[0], out var major) && major > 0) return major.ToString();
        }
        // engines.node in package.json
        try
        {
            var pkg = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "package.json")));
            if (pkg.RootElement.TryGetProperty("engines", out var engines) &&
                engines.TryGetProperty("node", out var nodeV))
            {
                var raw = nodeV.GetString()?.Trim('^', '~', '>', '=', '<', ' ') ?? "";
                if (int.TryParse(raw.Split('.')[0], out var major) && major > 0) return major.ToString();
            }
        }
        catch { }
        return "20"; // LTS default
    }

    private static string DetectNodeInstallCommand(string dir)
    {
        if (File.Exists(Path.Combine(dir, "pnpm-lock.yaml"))) return "pnpm install";
        if (File.Exists(Path.Combine(dir, "yarn.lock"))) return "yarn install";
        if (File.Exists(Path.Combine(dir, "bun.lockb"))) return "bun install";
        return "npm install";
    }

    private static string DetectDotnetVersion(string dir)
    {
        foreach (var csproj in Directory.EnumerateFiles(dir, "*.csproj", SearchOption.AllDirectories).Take(5))
        {
            try
            {
                var content = File.ReadAllText(csproj);
                var match = Regex.Match(content, @"<TargetFramework>net(\d+\.\d+)</TargetFramework>");
                if (match.Success) return match.Groups[1].Value;
                var matchShort = Regex.Match(content, @"<TargetFramework>net(\d+)</TargetFramework>");
                if (matchShort.Success) return matchShort.Groups[1].Value + ".0";
            }
            catch { }
        }
        return "10.0";
    }

    private static string DetectPythonVersion(string dir)
    {
        var pv = Path.Combine(dir, ".python-version");
        if (File.Exists(pv))
        {
            var v = File.ReadAllText(pv).Trim();
            if (!string.IsNullOrEmpty(v)) return v;
        }
        return "3.12";
    }

    private static string DetectGoVersion(string dir)
    {
        try
        {
            var gomod = File.ReadAllText(Path.Combine(dir, "go.mod"));
            var match = Regex.Match(gomod, @"^go\s+(\d+\.\d+)", RegexOptions.Multiline);
            if (match.Success) return match.Groups[1].Value;
        }
        catch { }
        return "1.22";
    }

    private static string DetectRubyVersion(string dir)
    {
        var rv = Path.Combine(dir, ".ruby-version");
        if (File.Exists(rv))
        {
            var v = File.ReadAllText(rv).Trim();
            if (!string.IsNullOrEmpty(v)) return v;
        }
        return "3.3";
    }

    // ── Ticket analysis ────────────────────────────────────────────────────

    public Task<TicketAnalysis> AnalyzeTicketAsync(
        Ticket ticket,
        IReadOnlyList<RepoContext> repoContexts,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var gaps = new List<Gap>();
        var description = ticket.Description ?? string.Empty;
        var ac = ticket.AcceptanceCriteria ?? string.Empty;

        // Rule: no description
        if (string.IsNullOrWhiteSpace(description) || description.Length < 20)
            gaps.Add(new Gap(GapSeverity.Error, "no-description", "Ticket has no meaningful description."));

        // Rule: no acceptance criteria
        if (string.IsNullOrWhiteSpace(ac) && !ContainsChecklist(description))
            gaps.Add(new Gap(GapSeverity.Error, "no-acceptance-criteria", "No acceptance criteria or checklist found."));

        // Rule: TBD/TODO markers
        foreach (var match in TbdPattern().Matches(description + " " + ac).Cast<Match>())
            gaps.Add(new Gap(GapSeverity.Warning, "contains-tbd", $"Contains \"{match.Value}\" — needs a decision."));

        // Rule: vague language
        foreach (var match in VaguePattern().Matches(description + " " + ac).Cast<Match>())
            gaps.Add(new Gap(GapSeverity.Warning, "vague-language", $"Vague: \"{match.Value}\" — add measurable criteria."));

        // Rule: no assignee
        if (string.IsNullOrWhiteSpace(ticket.Assignee))
            gaps.Add(new Gap(GapSeverity.Info, "no-assignee", "Ticket is unassigned."));

        // Rule: no estimate
        if (ticket.StoryPoints is null or 0)
            gaps.Add(new Gap(GapSeverity.Info, "no-estimate", "No story points estimated."));

        // Rule: open blockers
        if (ticket.LinkedIssues is not null)
        {
            foreach (var linked in ticket.LinkedIssues.Where(l =>
                l.LinkType.Contains("block", StringComparison.OrdinalIgnoreCase) &&
                !l.Status.Contains("done", StringComparison.OrdinalIgnoreCase) &&
                !l.Status.Contains("closed", StringComparison.OrdinalIgnoreCase)))
            {
                gaps.Add(new Gap(GapSeverity.Error, "open-blockers",
                    $"Blocked by {linked.Key} ({linked.Status})."));
            }
        }

        // Task extraction: from AC checklist, sub-tasks, or description paragraphs
        var tasks = ExtractTasks(ticket);

        // Relevant files: keyword match against repo context
        var relevantFiles = FindRelevantFiles(ticket, repoContexts);

        var analysis = new TicketAnalysis
        {
            TicketKey = ticket.Key,
            AgentUsed = Id,
            Gaps = gaps,
            Tasks = tasks,
            RelevantFiles = relevantFiles
        };

        return Task.FromResult(analysis);
    }

    // ── Code review ────────────────────────────────────────────────────────

    public Task<CodeReview> ReviewChangesAsync(string diff, RepoContext context, CancellationToken ct = default)
    {
        var findings = new List<SecurityFinding>();
        var quality = new List<QualityFinding>();

        var lines = diff.Split('\n');
        string? currentFile = null;
        var lineNum = 0;

        foreach (var line in lines)
        {
            if (line.StartsWith("+++ b/"))
            {
                currentFile = line["+++ b/".Length..];
                lineNum = 0;
                continue;
            }
            if (line.StartsWith("@@"))
            {
                var match = HunkHeaderPattern().Match(line);
                if (match.Success) lineNum = int.Parse(match.Groups[1].Value);
                continue;
            }
            if (!line.StartsWith('+') || line.StartsWith("+++")) continue;
            lineNum++;

            var addedLine = line[1..];

            // Security patterns
            if (HardcodedSecretPattern().IsMatch(addedLine))
                findings.Add(new SecurityFinding("high", "hardcoded-secret", currentFile ?? "?", lineNum, "Possible hardcoded secret or password."));
            if (SqlInjectionPattern().IsMatch(addedLine))
                findings.Add(new SecurityFinding("high", "sql-injection", currentFile ?? "?", lineNum, "String concatenation in SQL — use parameterized queries."));
            if (addedLine.Contains("innerHTML", StringComparison.OrdinalIgnoreCase) ||
                addedLine.Contains("dangerouslySetInnerHTML", StringComparison.Ordinal))
                findings.Add(new SecurityFinding("medium", "xss-risk", currentFile ?? "?", lineNum, "Direct HTML injection — sanitize input."));

            // Quality patterns
            if (addedLine.Contains("console.log(", StringComparison.Ordinal) ||
                addedLine.Contains("Console.WriteLine(", StringComparison.Ordinal))
                quality.Add(new QualityFinding("debug-output", currentFile ?? "?", $"Debug output left in (line {lineNum})."));
            if (addedLine.Contains("TODO", StringComparison.OrdinalIgnoreCase) ||
                addedLine.Contains("FIXME", StringComparison.OrdinalIgnoreCase))
                quality.Add(new QualityFinding("todo-left", currentFile ?? "?", $"TODO/FIXME marker at line {lineNum}."));
        }

        return Task.FromResult(new CodeReview
        {
            AgentUsed = Id,
            SecurityFindings = findings,
            QualityFindings = quality
        });
    }

    // ── Plan generation ────────────────────────────────────────────────────

    public Task<ImplementationPlan> GeneratePlanAsync(
        Ticket ticket,
        TicketAnalysis analysis,
        IReadOnlyList<RepoContext> repoContexts,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var plan = new ImplementationPlan
        {
            TicketKey = ticket.Key,
            AgentUsed = Id,
            Status = PlanStatus.Draft,
            Approach = $"Implement {ticket.Key}: {ticket.Summary}. " +
                       $"{analysis.Tasks.Count} task(s) identified from the ticket. " +
                       $"{analysis.RelevantFiles.Count} file(s) likely to change.",
            Tasks = analysis.Tasks
                .Select(t => new PlannedTask(t.Order, t.Description, null, t.FromTicket ? "From ticket AC" : "Inferred"))
                .ToList(),
            PlannedChanges = analysis.RelevantFiles
                .Select(f => new FileChange(f.Path, ChangeType.Modify, f.Reason))
                .ToList(),
            Assumptions = analysis.Gaps
                .Where(g => g.Severity == GapSeverity.Warning)
                .Select(g => g.Detail)
                .ToList(),
            OpenQuestions = analysis.Gaps
                .Where(g => g.Severity == GapSeverity.Error)
                .Select(g => new OpenQuestion(g.Detail, Array.Empty<string>()))
                .ToList()
        };

        return Task.FromResult(plan);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static string BuildDirectoryTree(string root, int maxDepth)
    {
        var sb = new StringBuilder();
        var exclude = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "node_modules", "bin", "obj", ".git", "dist", ".next", "__pycache__",
            "venv", ".venv", "vendor", "target", ".lightsoff", ".vs", ".idea"
        };
        BuildTree(sb, root, "", 0, maxDepth, exclude);
        return sb.ToString().TrimEnd();
    }

    private static void BuildTree(StringBuilder sb, string dir, string prefix, int depth, int maxDepth, HashSet<string> exclude)
    {
        if (depth >= maxDepth) return;
        var entries = Directory.EnumerateFileSystemEntries(dir)
            .Where(e => !exclude.Contains(Path.GetFileName(e)))
            .OrderBy(e => !Directory.Exists(e))
            .ThenBy(Path.GetFileName)
            .ToList();

        for (var i = 0; i < entries.Count && i < 30; i++)
        {
            var name = Path.GetFileName(entries[i]);
            var isDir = Directory.Exists(entries[i]);
            var isLast = i == Math.Min(entries.Count, 30) - 1;
            sb.AppendLine($"{prefix}{(isLast ? "└── " : "├── ")}{name}{(isDir ? "/" : "")}");
            if (isDir)
            {
                BuildTree(sb, entries[i], prefix + (isLast ? "    " : "│   "), depth + 1, maxDepth, exclude);
            }
        }
    }

    private static List<string> FindEntryPoints(string repoPath, TechStackDetector.DetectionResult stack)
    {
        var patterns = new[] { "Program.cs", "Startup.cs", "main.ts", "main.tsx", "index.ts", "index.js", "app.ts", "app.py", "main.py", "main.go", "main.rs" };
        return patterns
            .SelectMany(p => Directory.EnumerateFiles(repoPath, p, SearchOption.AllDirectories))
            .Select(f => Path.GetRelativePath(repoPath, f))
            .Where(f => !f.Contains("node_modules") && !f.Contains("bin/") && !f.Contains("obj/"))
            .Take(10)
            .ToList();
    }

    private static List<string> FindConfigFiles(string repoPath)
    {
        var names = new[] { "appsettings.json", "appsettings.Development.json", ".env.example", ".env",
            "docker-compose.yml", "docker-compose.yaml", "Dockerfile",
            "tsconfig.json", "nest-cli.json", "vite.config.ts", "webpack.config.js",
            ".eslintrc.js", ".eslintrc.json", "prettier.config.js" };
        var ci = new[] { ".github/workflows", ".gitlab-ci.yml", ".circleci/config.yml" };

        var result = names
            .Where(n => File.Exists(Path.Combine(repoPath, n)))
            .ToList();

        foreach (var ciPath in ci)
        {
            var full = Path.Combine(repoPath, ciPath);
            if (Directory.Exists(full))
            {
                result.AddRange(Directory.EnumerateFiles(full, "*", SearchOption.TopDirectoryOnly)
                    .Select(f => Path.GetRelativePath(repoPath, f)));
            }
            else if (File.Exists(full))
            {
                result.Add(ciPath);
            }
        }

        return result;
    }

    private static List<ApiEndpoint> FindApiEndpoints(string repoPath, TechStackDetector.DetectionResult stack)
    {
        var results = new List<ApiEndpoint>();
        var patterns = stack.Language switch
        {
            "C#" or _ when stack.Framework == ".NET" => new[] { @"(?:MapGet|MapPost|MapPut|MapDelete|MapPatch)\s*\(\s*""([^""]+)""", @"\[Http(Get|Post|Put|Delete|Patch)\s*\(\s*""([^""]*)""\s*\)\]" },
            "TypeScript" or "TypeScript/JavaScript" => new[] { @"(?:\.get|\.post|\.put|\.delete|\.patch)\s*\(\s*['""]([^'""]+)['""]", @"@(Get|Post|Put|Delete|Patch)\s*\(\s*['""]([^'""]*)['""]" },
            "Python" => new[] { @"@(?:app|router)\.(get|post|put|delete)\s*\(\s*['""]([^'""]+)['""]" },
            _ => Array.Empty<string>()
        };

        if (patterns.Length == 0) return results;

        var sourceFiles = Directory.EnumerateFiles(repoPath, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs") || f.EndsWith(".ts") || f.EndsWith(".js") || f.EndsWith(".py"))
            .Where(f => !f.Contains("node_modules") && !f.Contains("bin/") && !f.Contains("obj/") && !f.Contains("dist/"))
            .Take(200);

        foreach (var file in sourceFiles)
        {
            var lines = File.ReadAllLines(file);
            var relPath = Path.GetRelativePath(repoPath, file);
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (var pattern in patterns)
                {
                    var match = Regex.Match(lines[i], pattern);
                    if (!match.Success) continue;
                    var route = match.Groups[match.Groups.Count - 1].Value;
                    var method = match.Groups.Count > 2 ? match.Groups[1].Value.ToUpperInvariant() : InferMethod(lines[i]);
                    results.Add(new ApiEndpoint(method, route, relPath, i + 1));
                }
            }
        }

        return results.Take(100).ToList();
    }

    private static string InferMethod(string line)
    {
        var lower = line.ToLowerInvariant();
        if (lower.Contains("post")) return "POST";
        if (lower.Contains("put")) return "PUT";
        if (lower.Contains("delete")) return "DELETE";
        if (lower.Contains("patch")) return "PATCH";
        return "GET";
    }

    private static List<DataEntity> FindDataEntities(string repoPath, TechStackDetector.DetectionResult stack)
    {
        var results = new List<DataEntity>();
        var patterns = stack.Language switch
        {
            "C#" => new[] { @"class\s+(\w+)\s*(?::\s*\w+)?.*(?:DbSet|Entity|Table)" },
            "TypeScript" or "TypeScript/JavaScript" => new[] { @"@Entity\s*\(\s*\)\s*(?:export\s+)?class\s+(\w+)", @"model\s+(\w+)\s*\{" },
            "Python" => new[] { @"class\s+(\w+)\s*\(.*Model\s*\)" },
            _ => Array.Empty<string>()
        };

        if (patterns.Length == 0) return results;

        var files = Directory.EnumerateFiles(repoPath, "*.*", SearchOption.AllDirectories)
            .Where(f => !f.Contains("node_modules") && !f.Contains("bin/") && !f.Contains("obj/"))
            .Take(300);

        foreach (var file in files)
        {
            var content = File.ReadAllText(file);
            var relPath = Path.GetRelativePath(repoPath, file);
            foreach (var pattern in patterns)
            {
                foreach (Match match in Regex.Matches(content, pattern))
                {
                    results.Add(new DataEntity(match.Groups[1].Value, relPath));
                }
            }
        }

        // Also check for Prisma schema
        var prisma = Path.Combine(repoPath, "prisma", "schema.prisma");
        if (File.Exists(prisma))
        {
            foreach (Match match in Regex.Matches(File.ReadAllText(prisma), @"model\s+(\w+)\s*\{"))
            {
                results.Add(new DataEntity(match.Groups[1].Value, "prisma/schema.prisma"));
            }
        }

        return results.DistinctBy(e => e.Name).ToList();
    }

    private static RepoTestSetup? DetectTestSetup(string repoPath, TechStackDetector.DetectionResult stack)
    {
        if (stack.TestFramework is null) return null;

        var testDirs = new[] { "tests", "test", "__tests__", "spec", "Tests" }
            .Where(d => Directory.Exists(Path.Combine(repoPath, d)))
            .ToList();

        // Also check for inline test files
        if (testDirs.Count == 0)
        {
            var hasTestFiles = Directory.EnumerateFiles(repoPath, "*.test.*", SearchOption.AllDirectories)
                .Concat(Directory.EnumerateFiles(repoPath, "*.spec.*", SearchOption.AllDirectories))
                .Where(f => !f.Contains("node_modules"))
                .Any();
            if (hasTestFiles) testDirs.Add("(inline)");
        }

        string? configFile = null;
        var testConfigs = new[] { "jest.config.ts", "jest.config.js", "vitest.config.ts", "pytest.ini", "setup.cfg", "xunit.runner.json" };
        foreach (var tc in testConfigs)
        {
            if (File.Exists(Path.Combine(repoPath, tc)))
            {
                configFile = tc;
                break;
            }
        }

        return new RepoTestSetup(stack.TestFramework, configFile, testDirs);
    }

    private static List<RepoDependency> ParseDependencies(string repoPath, TechStackDetector.DetectionResult stack)
    {
        var pkgJson = Path.Combine(repoPath, "package.json");
        if (File.Exists(pkgJson))
        {
            try
            {
                var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(pkgJson));
                var deps = new List<RepoDependency>();
                if (doc.RootElement.TryGetProperty("dependencies", out var d))
                {
                    foreach (var prop in d.EnumerateObject().Take(20))
                    {
                        deps.Add(new RepoDependency(prop.Name, prop.Value.GetString() ?? "?"));
                    }
                }
                return deps;
            }
            catch { }
        }
        return new List<RepoDependency>();
    }

    private static List<GitCommitSummary> GetRecentCommits(string repoPath)
    {
        try
        {
            using var repo = new LibGit2Sharp.Repository(repoPath);
            return repo.Commits.Take(20)
                .Select(c => new GitCommitSummary(
                    c.Sha,
                    c.MessageShort,
                    c.Author.Name,
                    c.Author.When))
                .ToList();
        }
        catch
        {
            return new List<GitCommitSummary>();
        }
    }

    private static string? ReadReadmeSummary(string repoPath)
    {
        var readme = Path.Combine(repoPath, "README.md");
        if (!File.Exists(readme)) readme = Path.Combine(repoPath, "readme.md");
        if (!File.Exists(readme)) return null;
        try
        {
            var text = File.ReadAllText(readme);
            return text.Length > 500 ? text[..500] + "…" : text;
        }
        catch { return null; }
    }

    private static bool ContainsChecklist(string text) =>
        text.Contains("- [ ]", StringComparison.Ordinal) ||
        text.Contains("- [x]", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("* [ ]", StringComparison.Ordinal) ||
        text.Contains("[ ]", StringComparison.Ordinal) ||
        text.Contains("[x]", StringComparison.OrdinalIgnoreCase);

    private static List<TicketTask> ExtractTasks(Ticket ticket)
    {
        var tasks = new List<TicketTask>();
        var order = 1;

        // From sub-tasks
        if (ticket.SubTasks is { Count: > 0 })
        {
            foreach (var sub in ticket.SubTasks)
            {
                tasks.Add(new TicketTask(order++, $"[{sub.Key}] {sub.Summary}", true));
            }
        }

        // From checklist in description/AC
        var text = (ticket.AcceptanceCriteria ?? "") + "\n" + (ticket.Description ?? "");
        foreach (Match match in ChecklistPattern().Matches(text))
        {
            var taskText = StripHtml(match.Groups[1].Value.Trim());
            if (!string.IsNullOrWhiteSpace(taskText))
            {
                tasks.Add(new TicketTask(order++, taskText, true));
            }
        }

        // If no tasks found, create one from the summary
        if (tasks.Count == 0)
        {
            tasks.Add(new TicketTask(1, ticket.Summary, false));
        }

        return tasks;
    }

    private static string StripHtml(string html) =>
        Regex.Replace(html, "<[^>]+>", "").Trim();

    private static List<RelevantFile> FindRelevantFiles(Ticket ticket, IReadOnlyList<RepoContext> contexts)
    {
        var results = new List<RelevantFile>();
        var keywords = ExtractKeywords(ticket.Summary + " " + (ticket.Description ?? ""));

        foreach (var ctx in contexts)
        {
            foreach (var ep in ctx.ApiSurface)
            {
                if (ep.File is not null && keywords.Any(k => ep.Route.Contains(k, StringComparison.OrdinalIgnoreCase)))
                {
                    results.Add(new RelevantFile(ep.File, $"Route '{ep.Route}' matches ticket keywords"));
                }
            }
            foreach (var entity in ctx.DataModel)
            {
                if (entity.File is not null && keywords.Any(k => entity.Name.Contains(k, StringComparison.OrdinalIgnoreCase)))
                {
                    results.Add(new RelevantFile(entity.File, $"Entity '{entity.Name}' matches ticket keywords"));
                }
            }
        }

        return results.DistinctBy(f => f.Path).Take(20).ToList();
    }

    private static List<string> ExtractKeywords(string text)
    {
        var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the", "a", "an", "is", "are", "was", "were", "be", "been", "being",
            "have", "has", "had", "do", "does", "did", "will", "would", "shall",
            "should", "may", "might", "must", "can", "could", "to", "of", "in",
            "for", "on", "with", "at", "by", "from", "as", "into", "through",
            "and", "but", "or", "nor", "not", "no", "so", "yet", "both", "either",
            "this", "that", "these", "those", "it", "its", "we", "our", "they"
        };

        return Regex.Matches(text, @"\b[a-zA-Z]{3,}\b")
            .Select(m => m.Value.ToLowerInvariant())
            .Where(w => !stopWords.Contains(w))
            .Distinct()
            .Take(15)
            .ToList();
    }

    [GeneratedRegex(@"\b(?:TBD|TODO|to be (?:decided|defined|determined))\b", RegexOptions.IgnoreCase)]
    private static partial Regex TbdPattern();

    [GeneratedRegex(@"\b(?:should work|properly|as expected|correctly|ASAP|as needed)\b", RegexOptions.IgnoreCase)]
    private static partial Regex VaguePattern();

    [GeneratedRegex(@"(?:[-*]\s*)?\[[ x]\]\s*(.+)", RegexOptions.IgnoreCase)]
    private static partial Regex ChecklistPattern();

    [GeneratedRegex(@"(?:password|secret|token|api_?key)\s*[:=]\s*[""'][^""']{4,}[""']", RegexOptions.IgnoreCase)]
    private static partial Regex HardcodedSecretPattern();

    [GeneratedRegex(@"(?:""[^""]*""\s*\+|\+\s*""[^""]*"").*(?:SELECT|INSERT|UPDATE|DELETE|DROP|WHERE)", RegexOptions.IgnoreCase)]
    private static partial Regex SqlInjectionPattern();

    [GeneratedRegex(@"@@\s*-(\d+)")]
    private static partial Regex HunkHeaderPattern();
}
