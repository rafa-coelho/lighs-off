using LightsOff.Core.Utilities;

namespace LightsOff.Tests;

public class TextSlugTests
{
    [Theory]
    [InlineData("Fix login bug", "fix-login-bug")]
    [InlineData("  Fix   LOGIN   bug  ", "fix-login-bug")]
    [InlineData("Área do usuário", "area-do-usuario")]
    [InlineData("", "")]
    public void Slugify_produces_expected_output(string input, string expected)
    {
        Assert.Equal(expected, TextSlug.Slugify(input));
    }

    [Fact]
    public void BuildBranchName_replaces_placeholders()
    {
        var branch = TextSlug.BuildBranchName("feature/{key}-{slug}", "PROJ-1", "Fix login bug");
        Assert.Equal("feature/PROJ-1-fix-login-bug", branch);
    }
}
