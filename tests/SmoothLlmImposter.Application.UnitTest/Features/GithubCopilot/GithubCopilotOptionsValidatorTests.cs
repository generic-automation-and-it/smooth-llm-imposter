using SmoothLlmImposter.Application.Features.GithubCopilot;

namespace SmoothLlmImposter.Application.UnitTest.Features.GithubCopilot;

public sealed class GithubCopilotOptionsValidatorTests
{
    private readonly GithubCopilotOptionsValidator _validator = new();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Enabled_without_a_database_path_fails_and_names_the_setting(string? databasePath)
    {
        var result = _validator.Validate(null, new GithubCopilotOptions { Enabled = true, DatabasePath = databasePath });

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("GithubCopilot__DatabasePath");
    }

    [Fact]
    public void Enabled_with_a_database_path_succeeds()
    {
        var result = _validator.Validate(
            null, new GithubCopilotOptions { Enabled = true, DatabasePath = "/data/github-copilot/copilot.db" });

        result.Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Disabled_without_a_database_path_succeeds()
    {
        _validator.Validate(null, new GithubCopilotOptions { Enabled = false }).Succeeded.ShouldBeTrue();
    }
}
