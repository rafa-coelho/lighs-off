using LibGit2Sharp;
using LightsOff.Infrastructure.Agents;
using Microsoft.Extensions.Logging.Abstractions;

namespace LightsOff.Tests;

/// <summary>
/// End-to-end test for the local coverage parser: builds a real temp git repo, commits a base
/// file, creates a feature branch with an added file, writes a cobertura report at the expected
/// path, and asserts the computed new-code coverage matches what the report says about the new
/// lines. Also covers the "no artifact" error path.
/// </summary>
public class LocalCodeAgentCoverageTests : IDisposable
{
    private readonly string _repoPath;

    public LocalCodeAgentCoverageTests()
    {
        _repoPath = Path.Combine(Path.GetTempPath(), "lightsoff-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_repoPath);
    }

    public void Dispose()
    {
        try
        {
            // LibGit2Sharp holds file handles on Windows; on macOS delete is clean.
            Directory.Delete(_repoPath, recursive: true);
        }
        catch { }
    }

    [Fact]
    public async Task Returns_error_when_no_coverage_artifact_is_present()
    {
        InitRepoWithInitialCommit();

        var agent = new LocalCodeAgent(NullLogger<LocalCodeAgent>.Instance);
        var report = await agent.MeasureNewCodeCoverageAsync(_repoPath, baseBranch: "main");

        Assert.NotNull(report.Error);
        Assert.Contains("No coverage artifact", report.Error);
    }

    [Fact]
    public async Task Cobertura_new_code_coverage_matches_added_lines()
    {
        InitRepoWithInitialCommit();

        // Create a feature branch, add a new file with 4 lines.
        using (var git = new Repository(_repoPath))
        {
            var branch = git.CreateBranch("feature/x");
            Commands.Checkout(git, branch);
        }
        var newFilePath = Path.Combine(_repoPath, "src", "new.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(newFilePath)!);
        File.WriteAllText(newFilePath, "line1\nline2\nline3\nline4\n");
        using (var git = new Repository(_repoPath))
        {
            Commands.Stage(git, "*");
            var sig = new Signature("test", "test@test", DateTimeOffset.UtcNow);
            git.Commit("add new.cs", sig, sig);
        }

        // Cobertura report: of the 4 new lines, hit lines 1, 2, 3 (75%). Line 4 is the file's
        // final newline so we pretend it's uncovered.
        var coveragePath = Path.Combine(_repoPath, "coverage.cobertura.xml");
        File.WriteAllText(coveragePath, @"<?xml version=""1.0"" ?>
<coverage>
  <packages>
    <package name=""pkg"">
      <classes>
        <class filename=""src/new.cs"">
          <lines>
            <line number=""1"" hits=""1"" />
            <line number=""2"" hits=""3"" />
            <line number=""3"" hits=""1"" />
            <line number=""4"" hits=""0"" />
          </lines>
        </class>
      </classes>
    </package>
  </packages>
</coverage>");

        var agent = new LocalCodeAgent(NullLogger<LocalCodeAgent>.Instance);
        var report = await agent.MeasureNewCodeCoverageAsync(_repoPath, baseBranch: "main");

        Assert.Null(report.Error);
        Assert.Equal(4, report.NewCodeLinesTotal);
        Assert.Equal(3, report.NewCodeLinesCovered);
        Assert.Equal(75d, report.NewCodeLinePercent);

        var file = Assert.Single(report.Files);
        Assert.Equal("src/new.cs", file.Path);
        Assert.Equal(new[] { 4 }, file.UncoveredNewLines);
    }

    [Fact]
    public async Task Lcov_report_parses_and_diffs_correctly()
    {
        InitRepoWithInitialCommit();

        using (var git = new Repository(_repoPath))
        {
            var branch = git.CreateBranch("feature/y");
            Commands.Checkout(git, branch);
        }
        var newFile = Path.Combine(_repoPath, "lib", "util.ts");
        Directory.CreateDirectory(Path.GetDirectoryName(newFile)!);
        File.WriteAllText(newFile, "line1\nline2\n");
        using (var git = new Repository(_repoPath))
        {
            Commands.Stage(git, "*");
            var sig = new Signature("test", "test@test", DateTimeOffset.UtcNow);
            git.Commit("add util.ts", sig, sig);
        }

        // lcov format: both lines covered.
        Directory.CreateDirectory(Path.Combine(_repoPath, "coverage"));
        File.WriteAllText(Path.Combine(_repoPath, "coverage", "lcov.info"),
            "SF:lib/util.ts\nDA:1,3\nDA:2,1\nend_of_record\n");

        var agent = new LocalCodeAgent(NullLogger<LocalCodeAgent>.Instance);
        var report = await agent.MeasureNewCodeCoverageAsync(_repoPath, baseBranch: "main");

        Assert.Null(report.Error);
        Assert.Equal(2, report.NewCodeLinesTotal);
        Assert.Equal(2, report.NewCodeLinesCovered);
        Assert.Equal(100d, report.NewCodeLinePercent);
    }

    private void InitRepoWithInitialCommit()
    {
        Repository.Init(_repoPath);
        File.WriteAllText(Path.Combine(_repoPath, "README.md"), "# test\n");
        using var git = new Repository(_repoPath);
        Commands.Stage(git, "*");
        var sig = new Signature("test", "test@test", DateTimeOffset.UtcNow);
        git.Commit("initial", sig, sig);
        // Ensure the default branch is named "main" regardless of git defaults.
        if (git.Head.FriendlyName != "main")
        {
            var main = git.CreateBranch("main");
            Commands.Checkout(git, main);
        }
    }
}
