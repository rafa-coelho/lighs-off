using LightsOff.Core.Domain;

namespace LightsOff.Infrastructure.Agents;

public static class TechStackDetector
{
    public record DetectionResult(
        string Language,
        string? Framework,
        string? PackageManager,
        string? TestFramework,
        string? TestCommand);

    private static readonly (string File, string Language, string? Framework, string? PkgMgr, string? TestFw, string? TestCmd)[] Signatures = new[]
    {
        ("package.json",        "TypeScript/JavaScript", null,      null,     null,     "npm test"),
        ("tsconfig.json",       "TypeScript",            null,      null,     null,     null),
        ("angular.json",        "TypeScript",            "Angular", "npm",    "Karma",  "ng test"),
        ("next.config.js",      "TypeScript",            "Next.js", "npm",    "Jest",   "npm test"),
        ("next.config.mjs",     "TypeScript",            "Next.js", "npm",    "Jest",   "npm test"),
        ("nest-cli.json",       "TypeScript",            "NestJS",  "npm",    "Jest",   "npm test"),
        ("nuxt.config.ts",      "TypeScript",            "Nuxt",    "npm",    "Vitest", "npm test"),
        ("vite.config.ts",      "TypeScript",            "Vite",    "npm",    "Vitest", "npm test"),

        ("*.csproj",            "C#",                    ".NET",    "NuGet",  "xUnit",  "dotnet test"),
        ("*.sln",               "C#",                    ".NET",    "NuGet",  "xUnit",  "dotnet test"),
        ("*.slnx",              "C#",                    ".NET",    "NuGet",  "xUnit",  "dotnet test"),

        ("requirements.txt",    "Python",                null,      "pip",    "pytest", "pytest"),
        ("pyproject.toml",      "Python",                null,      "pip",    "pytest", "pytest"),
        ("setup.py",            "Python",                null,      "pip",    "pytest", "pytest"),

        ("go.mod",              "Go",                    null,      "go mod", null,     "go test ./..."),
        ("Cargo.toml",          "Rust",                  null,      "cargo",  null,     "cargo test"),
        ("pom.xml",             "Java",                  "Maven",   "Maven",  "JUnit",  "mvn test"),
        ("build.gradle",        "Java/Kotlin",           "Gradle",  "Gradle", "JUnit",  "gradle test"),
        ("build.gradle.kts",    "Kotlin",                "Gradle",  "Gradle", "JUnit",  "gradle test"),
        ("Gemfile",             "Ruby",                  "Rails",   "Bundler","RSpec",  "bundle exec rspec"),
        ("mix.exs",             "Elixir",                "Phoenix", "Mix",    "ExUnit", "mix test"),
        ("composer.json",       "PHP",                   "Laravel", "Composer","PHPUnit","vendor/bin/phpunit"),
    };

    public static DetectionResult Detect(string repoPath)
    {
        string? lang = null, fw = null, pm = null, testFw = null, testCmd = null;

        foreach (var (filePattern, language, framework, pkgMgr, testFramework, testCommand) in Signatures)
        {
            bool found;
            if (filePattern.StartsWith("*"))
            {
                var ext = filePattern[1..];
                found = Directory.EnumerateFiles(repoPath, $"*{ext}", SearchOption.TopDirectoryOnly).Any();
            }
            else
            {
                found = File.Exists(Path.Combine(repoPath, filePattern));
            }

            if (!found) continue;

            lang ??= language;
            fw ??= framework;
            pm ??= pkgMgr;
            testFw ??= testFramework;
            testCmd ??= testCommand;
        }

        // Refine package manager from lock files
        if (File.Exists(Path.Combine(repoPath, "pnpm-lock.yaml"))) pm = "pnpm";
        else if (File.Exists(Path.Combine(repoPath, "yarn.lock"))) pm = "yarn";
        else if (File.Exists(Path.Combine(repoPath, "bun.lockb"))) pm = "bun";
        else if (File.Exists(Path.Combine(repoPath, "package-lock.json"))) pm = "npm";

        // Detect test framework from config files
        if (File.Exists(Path.Combine(repoPath, "jest.config.ts")) || File.Exists(Path.Combine(repoPath, "jest.config.js")))
        {
            testFw = "Jest";
        }
        else if (File.Exists(Path.Combine(repoPath, "vitest.config.ts")))
        {
            testFw = "Vitest";
        }

        return new DetectionResult(
            lang ?? "Unknown",
            fw,
            pm,
            testFw,
            testCmd);
    }

    public static string FormatStack(DetectionResult result)
    {
        var parts = new List<string> { result.Language };
        if (result.Framework is not null) parts.Add(result.Framework);
        if (result.PackageManager is not null) parts.Add(result.PackageManager);
        return string.Join(" / ", parts);
    }
}
