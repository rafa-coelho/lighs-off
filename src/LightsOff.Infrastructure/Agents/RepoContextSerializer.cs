using System.Text;
using LightsOff.Core.Domain;

namespace LightsOff.Infrastructure.Agents;

public static class RepoContextSerializer
{
    private const string ContextDir = ".lightsoff";
    private const string ContextFile = "context.md";

    public static string GetContextPath(string repoPath) =>
        Path.Combine(repoPath, ContextDir, ContextFile);

    public static void Save(string repoPath, RepoContext ctx)
    {
        var dir = Path.Combine(repoPath, ContextDir);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ContextFile), ToMarkdown(ctx));

        // gitignore the .lightsoff directory
        var gitignorePath = Path.Combine(repoPath, ".gitignore");
        if (File.Exists(gitignorePath))
        {
            var content = File.ReadAllText(gitignorePath);
            if (!content.Contains(".lightsoff/"))
            {
                File.AppendAllText(gitignorePath, "\n.lightsoff/\n");
            }
        }
    }

    public static string ToMarkdown(RepoContext ctx)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {ctx.RepoName} — Repository Context");
        sb.AppendLine($"Generated: {ctx.AnalyzedAt:yyyy-MM-dd HH:mm} UTC by `{ctx.AgentUsed}` agent");
        sb.AppendLine();

        // Tech stack
        sb.AppendLine("## Tech Stack");
        sb.AppendLine(ctx.TechStack);
        sb.AppendLine();

        // Directory tree
        if (!string.IsNullOrWhiteSpace(ctx.DirectoryTree))
        {
            sb.AppendLine("## Directory Structure");
            sb.AppendLine("```");
            sb.AppendLine(ctx.DirectoryTree);
            sb.AppendLine("```");
            sb.AppendLine();
        }

        // Entry points
        if (ctx.EntryPoints.Count > 0)
        {
            sb.AppendLine("## Key Files");
            foreach (var ep in ctx.EntryPoints)
            {
                sb.AppendLine($"- Entry: `{ep}`");
            }
            sb.AppendLine();
        }

        // Config
        if (ctx.ConfigFiles.Count > 0)
        {
            sb.AppendLine("## Config Files");
            foreach (var cf in ctx.ConfigFiles)
            {
                sb.AppendLine($"- `{cf}`");
            }
            sb.AppendLine();
        }

        // API
        if (ctx.ApiSurface.Count > 0)
        {
            sb.AppendLine("## API Surface");
            foreach (var ep in ctx.ApiSurface.Take(50))
            {
                var loc = ep.File is not null ? $" ({ep.File}:{ep.Line})" : "";
                sb.AppendLine($"- `{ep.Method} {ep.Route}`{loc}");
            }
            sb.AppendLine();
        }

        // Data model
        if (ctx.DataModel.Count > 0)
        {
            sb.AppendLine("## Data Model");
            foreach (var e in ctx.DataModel)
            {
                sb.AppendLine($"- {e.Name}{(e.File is not null ? $" (`{e.File}`)" : "")}");
            }
            sb.AppendLine();
        }

        // Tests
        if (ctx.TestInfo is not null)
        {
            sb.AppendLine("## Test Setup");
            sb.AppendLine($"- Framework: {ctx.TestInfo.Framework}");
            if (ctx.TestInfo.ConfigFile is not null)
                sb.AppendLine($"- Config: `{ctx.TestInfo.ConfigFile}`");
            foreach (var dir in ctx.TestInfo.TestDirectories)
                sb.AppendLine($"- Tests: `{dir}`");
            sb.AppendLine();
        }

        // Dependencies
        if (ctx.TopDependencies.Count > 0)
        {
            sb.AppendLine("## Dependencies (top 20)");
            sb.AppendLine("| Package | Version |");
            sb.AppendLine("|---|---|");
            foreach (var dep in ctx.TopDependencies.Take(20))
            {
                sb.AppendLine($"| {dep.Name} | {dep.Version} |");
            }
            sb.AppendLine();
        }

        // Recent history
        if (ctx.RecentHistory.Count > 0)
        {
            sb.AppendLine("## Recent History");
            foreach (var c in ctx.RecentHistory.Take(20))
            {
                sb.AppendLine($"- `{c.Hash[..7]}` {c.Message} — {c.Author} ({FormatRelative(c.Date)})");
            }
            sb.AppendLine();
        }

        // README
        if (!string.IsNullOrWhiteSpace(ctx.ReadmeSummary))
        {
            sb.AppendLine("## README Summary");
            sb.AppendLine(ctx.ReadmeSummary);
            sb.AppendLine();
        }

        // AI-generated sections (only if filled)
        if (!string.IsNullOrWhiteSpace(ctx.ArchitectureOverview))
        {
            sb.AppendLine("## Architecture Overview");
            sb.AppendLine(ctx.ArchitectureOverview);
            sb.AppendLine();
        }

        if (ctx.Features is { Count: > 0 })
        {
            sb.AppendLine("## Features");
            foreach (var f in ctx.Features)
            {
                sb.AppendLine($"- **{f.Name}**{(f.Description is not null ? $": {f.Description}" : "")}");
            }
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(ctx.DataFlowDescription))
        {
            sb.AppendLine("## Data Flow");
            sb.AppendLine(ctx.DataFlowDescription);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string FormatRelative(DateTimeOffset when)
    {
        var delta = DateTimeOffset.UtcNow - when;
        if (delta.TotalDays >= 365) return $"{(int)(delta.TotalDays / 365)}y ago";
        if (delta.TotalDays >= 30) return $"{(int)(delta.TotalDays / 30)}mo ago";
        if (delta.TotalDays >= 1) return $"{(int)delta.TotalDays}d ago";
        if (delta.TotalHours >= 1) return $"{(int)delta.TotalHours}h ago";
        return $"{(int)delta.TotalMinutes}m ago";
    }
}
