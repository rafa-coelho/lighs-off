using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using LightsOff.Core.Abstractions;
using LightsOff.Core.Domain;

namespace LightsOff.Infrastructure.Agents;

public partial class LocalCodeAgent
{
    public Task<IAgentSession> StartSessionAsync(AgentSessionSpec spec, CancellationToken ct = default)
        => throw new NotSupportedException(
            "The local agent does not support streaming sessions. Switch to 'claude-cli' (or another session-capable agent) in Settings to run Implement/Validate.");

    public Task<ChatReplyEvent> ReplyToPlanChatAsync(ImplementationPlan plan, string userMessage, CancellationToken ct = default)
        => throw new NotSupportedException(
            "The local agent does not support chat replies. Switch to 'claude-cli' (or another AI agent) in Settings to use plan-replan chat.");

    public Task<ImplementationPlan> ReworkPlanAsync(Ticket ticket, ImplementationPlan previousPlan, string userFeedback, IReadOnlyList<RepoContext> repoContexts, CancellationToken ct = default)
        => throw new NotSupportedException(
            "The local agent cannot rework a plan from natural-language feedback. Switch to 'claude-cli' in Settings to use plan replan.");

    public Task<AcReviewReport> ReviewAgainstAcAsync(Ticket ticket, string diff, RepoContext context, CancellationToken ct = default)
        => throw new NotSupportedException(
            "The local agent cannot assess whether a diff satisfies a ticket's acceptance criteria. Switch to 'claude-cli' in Settings.");

    public Task<TicketAnalysis> RefineAnalysisAsync(Ticket ticket, TicketAnalysis previousAnalysis, IReadOnlyList<ChatMessage> conversation, IReadOnlyList<RepoContext> repoContexts, CancellationToken ct = default)
        => throw new NotSupportedException(
            "The local agent cannot refine analysis from natural-language feedback. Switch to 'claude-cli' in Settings.");

    // ── New-code coverage ─────────────────────────────────────────────────
    //
    // The local agent does not run tests itself — the pipeline's TestRunnerService does that, and
    // the repo's test config is expected to produce a coverage artifact on disk. Here we read
    // the artifact, diff against <paramref name="baseBranch"/> to find added lines, and compute
    // coverage on just those lines.
    //
    // Supported artifact formats (first match wins):
    //   coverage/lcov.info                               (lcov)
    //   coverage.cobertura.xml, coverage/coverage.xml    (cobertura, e.g. coverlet, pytest-cov)
    //   target/site/jacoco/jacoco.xml                    (jacoco — Java)
    //
    // If no artifact exists we return a report with <see cref="CoverageReport.Error"/> set so the
    // Validate step surfaces the config issue rather than failing the gate on phantom percentages.

    public Task<CoverageReport> MeasureNewCodeCoverageAsync(
        string repoPath,
        string baseBranch,
        CancellationToken ct = default)
    {
        var repoName = Path.GetFileName(repoPath);
        var report = new CoverageReport
        {
            RepositoryName = repoName,
            AgentUsed = Id
        };

        var artifactPath = FindCoverageArtifact(repoPath);
        if (artifactPath is null)
        {
            report.Error = "No coverage artifact found. Expected one of: coverage/lcov.info, " +
                           "coverage.cobertura.xml, coverage/coverage.xml, or target/site/jacoco/jacoco.xml. " +
                           "Configure your test command to emit coverage.";
            return Task.FromResult(report);
        }

        IReadOnlyDictionary<string, FileCoverage> fileCoverage;
        try
        {
            fileCoverage = ParseArtifact(artifactPath);
        }
        catch (Exception ex)
        {
            report.Error = $"Could not parse coverage artifact at {Path.GetRelativePath(repoPath, artifactPath)}: {ex.Message}";
            return Task.FromResult(report);
        }

        IReadOnlyDictionary<string, HashSet<int>> newLinesByFile;
        try
        {
            newLinesByFile = CollectAddedLines(repoPath, baseBranch);
        }
        catch (Exception ex)
        {
            report.Error = $"Could not diff against {baseBranch}: {ex.Message}";
            return Task.FromResult(report);
        }

        long overallTotal = 0, overallCovered = 0;
        var files = new List<CoverageFile>();

        foreach (var (relPath, cov) in fileCoverage)
        {
            overallTotal += cov.TotalLines;
            overallCovered += cov.CoveredLines;

            if (!newLinesByFile.TryGetValue(relPath, out var newLines) || newLines.Count == 0)
                continue;

            var newCovered = newLines.Count(l => cov.CoveredLineNumbers.Contains(l));
            var uncovered = newLines.Where(l => !cov.CoveredLineNumbers.Contains(l)).OrderBy(l => l).ToList();

            files.Add(new CoverageFile(
                Path: relPath,
                LinePercent: newLines.Count == 0 ? 100d : (newCovered * 100d / newLines.Count),
                NewLinesTotal: newLines.Count,
                NewLinesCovered: newCovered,
                UncoveredNewLines: uncovered));

            report.NewCodeLinesTotal += newLines.Count;
            report.NewCodeLinesCovered += newCovered;
        }

        report.Files = files;
        report.OverallLinePercent = overallTotal == 0 ? 0d : (overallCovered * 100d / overallTotal);
        report.NewCodeLinePercent = report.NewCodeLinesTotal == 0
            ? 100d  // no new code → nothing to gate on; treat as passing
            : (report.NewCodeLinesCovered * 100d / report.NewCodeLinesTotal);

        return Task.FromResult(report);
    }

    private static string? FindCoverageArtifact(string repoPath)
    {
        string[] candidates =
        {
            Path.Combine(repoPath, "coverage", "lcov.info"),
            Path.Combine(repoPath, "coverage.cobertura.xml"),
            Path.Combine(repoPath, "coverage", "coverage.cobertura.xml"),
            Path.Combine(repoPath, "coverage", "coverage.xml"),
            Path.Combine(repoPath, "coverage.xml"),
            Path.Combine(repoPath, "target", "site", "jacoco", "jacoco.xml"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private sealed record FileCoverage(int TotalLines, int CoveredLines, HashSet<int> CoveredLineNumbers);

    private static IReadOnlyDictionary<string, FileCoverage> ParseArtifact(string path)
    {
        var name = Path.GetFileName(path);
        if (name.Equals("lcov.info", StringComparison.OrdinalIgnoreCase))
            return ParseLcov(path);
        if (name.Equals("jacoco.xml", StringComparison.OrdinalIgnoreCase))
            return ParseJacoco(path);
        return ParseCobertura(path); // cobertura is the default for coverage.xml / .cobertura.xml
    }

    private static IReadOnlyDictionary<string, FileCoverage> ParseLcov(string path)
    {
        var result = new Dictionary<string, FileCoverage>(StringComparer.Ordinal);
        string? currentFile = null;
        var covered = new HashSet<int>();
        var total = 0;
        var hit = 0;

        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.StartsWith("SF:", StringComparison.Ordinal))
            {
                currentFile = NormalizePath(line[3..]);
                covered = new HashSet<int>();
                total = 0;
                hit = 0;
            }
            else if (line.StartsWith("DA:", StringComparison.Ordinal) && currentFile is not null)
            {
                // DA:<line>,<hits>
                var body = line[3..];
                var comma = body.IndexOf(',');
                if (comma < 0) continue;
                if (!int.TryParse(body.AsSpan(0, comma), out var lineNum)) continue;
                if (!int.TryParse(body.AsSpan(comma + 1), out var hits)) continue;
                total++;
                if (hits > 0)
                {
                    hit++;
                    covered.Add(lineNum);
                }
            }
            else if (line == "end_of_record" && currentFile is not null)
            {
                result[currentFile] = new FileCoverage(total, hit, covered);
                currentFile = null;
            }
        }

        return result;
    }

    private static IReadOnlyDictionary<string, FileCoverage> ParseCobertura(string path)
    {
        var doc = XDocument.Load(path);
        var result = new Dictionary<string, FileCoverage>(StringComparer.Ordinal);

        foreach (var cls in doc.Descendants("class"))
        {
            var file = (string?)cls.Attribute("filename");
            if (string.IsNullOrWhiteSpace(file)) continue;
            var norm = NormalizePath(file);

            var total = 0;
            var hit = 0;
            var covered = new HashSet<int>();

            foreach (var l in cls.Descendants("line"))
            {
                if (!int.TryParse((string?)l.Attribute("number"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var num))
                    continue;
                total++;
                if (long.TryParse((string?)l.Attribute("hits"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var hits) && hits > 0)
                {
                    hit++;
                    covered.Add(num);
                }
            }

            // Multiple <class> entries per file can exist (inner classes). Merge.
            if (result.TryGetValue(norm, out var existing))
            {
                covered.UnionWith(existing.CoveredLineNumbers);
                result[norm] = new FileCoverage(existing.TotalLines + total, existing.CoveredLines + hit, covered);
            }
            else
            {
                result[norm] = new FileCoverage(total, hit, covered);
            }
        }

        return result;
    }

    private static IReadOnlyDictionary<string, FileCoverage> ParseJacoco(string path)
    {
        var doc = XDocument.Load(path);
        var result = new Dictionary<string, FileCoverage>(StringComparer.Ordinal);

        foreach (var pkg in doc.Descendants("package"))
        {
            var pkgPath = ((string?)pkg.Attribute("name") ?? "").Replace('.', '/');
            foreach (var src in pkg.Elements("sourcefile"))
            {
                var fileName = (string?)src.Attribute("name");
                if (string.IsNullOrWhiteSpace(fileName)) continue;
                var norm = NormalizePath(Path.Combine(pkgPath, fileName));

                var total = 0;
                var hit = 0;
                var covered = new HashSet<int>();

                foreach (var l in src.Elements("line"))
                {
                    if (!int.TryParse((string?)l.Attribute("nr"), out var num)) continue;
                    if (!int.TryParse((string?)l.Attribute("ci"), out var ci)) continue;  // covered-instructions
                    if (!int.TryParse((string?)l.Attribute("mi"), out var mi)) continue;  // missed-instructions
                    if (ci + mi == 0) continue;
                    total++;
                    if (ci > 0)
                    {
                        hit++;
                        covered.Add(num);
                    }
                }

                result[norm] = new FileCoverage(total, hit, covered);
            }
        }

        return result;
    }

    private static IReadOnlyDictionary<string, HashSet<int>> CollectAddedLines(string repoPath, string baseBranch)
    {
        using var git = new LibGit2Sharp.Repository(repoPath);
        var baseCommit = git.Branches[baseBranch]?.Tip
                         ?? git.Branches[$"origin/{baseBranch}"]?.Tip
                         ?? throw new InvalidOperationException($"Base branch '{baseBranch}' not found.");

        var headCommit = git.Head.Tip
                         ?? throw new InvalidOperationException("HEAD has no commits.");

        var patch = git.Diff.Compare<LibGit2Sharp.Patch>(baseCommit.Tree, headCommit.Tree);

        var byFile = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        foreach (var change in patch)
        {
            if (change.Status is LibGit2Sharp.ChangeKind.Deleted) continue;
            var norm = NormalizePath(change.Path);
            var set = new HashSet<int>();

            // Parse hunk headers from the patch text to find added line numbers.
            // A hunk header looks like "@@ -12,3 +15,7 @@"; then each subsequent '+' line
            // (excluding "+++") is an added line at consecutive numbers starting at +15.
            var lines = change.Patch.Split('\n');
            var currentLine = 0;
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var match = HunkHeaderStartPattern().Match(line);
                if (match.Success)
                {
                    currentLine = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                    continue;
                }
                if (line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal))
                    continue;
                if (line.StartsWith('+'))
                {
                    set.Add(currentLine);
                    currentLine++;
                }
                else if (line.StartsWith(' '))
                {
                    currentLine++;
                }
                // '-' lines don't advance the new-file line counter
            }

            if (set.Count > 0)
                byFile[norm] = set;
        }

        return byFile;
    }

    private static string NormalizePath(string path) =>
        path.Replace('\\', '/').TrimStart('.').TrimStart('/');

    [GeneratedRegex(@"^@@\s+-\d+(?:,\d+)?\s+\+(\d+)(?:,\d+)?\s+@@")]
    private static partial Regex HunkHeaderStartPattern();
}
