using SmoothLlmImposter.Domain.GithubCopilot;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Domain.UnitTest.GithubCopilot;

/// <summary>
/// L0 — exact terminal-suffix recognition of the reserved Copilot provider keys, and the prefix slug rules
/// that make two operators' prefixes collide only when they genuinely name the same account pair.
/// </summary>
public class GithubCopilotProfileKeyTests
{
    [Theory]
    [InlineData("github-anthropic", "", ApiDialect.Anthropic)]
    [InlineData("github-openai", "", ApiDialect.OpenAi)]
    [InlineData("company1-github-anthropic", "company1", ApiDialect.Anthropic)]
    [InlineData("company1-github-openai", "company1", ApiDialect.OpenAi)]
    [InlineData("a-1-github-openai", "a-1", ApiDialect.OpenAi)]
    public void Parses_reserved_keys_and_derives_the_prefix(string key, string prefix, ApiDialect dialect)
    {
        GithubCopilotProfileKey.TryParse(key, out GithubCopilotProfile? profile).ShouldBeTrue();
        profile!.Prefix.ShouldBe(prefix);
        profile.Dialect.ShouldBe(dialect);
        profile.Key.ShouldBe(key);
    }

    [Theory]
    [InlineData("my-github-openai-proxy")]
    [InlineData("github-anthropic-proxy")]
    [InlineData("github")]
    [InlineData("GITHUB-OPENAI")]
    [InlineData("Company1-github-openai")]
    [InlineData("-github-openai")]
    [InlineData("company--1-github-openai")]
    [InlineData("company_1-github-openai")]
    [InlineData("github-openai-")]
    public void Rejects_non_terminal_non_canonical_and_malformed_keys(string key)
    {
        GithubCopilotProfileKey.TryParse(key, out GithubCopilotProfile? profile).ShouldBeFalse();
        profile.ShouldBeNull();
        GithubCopilotProfileKey.IsReservedKey(key).ShouldBeFalse();
    }

    [Fact]
    public void Generates_both_keys_of_a_pair()
    {
        var profile = new GithubCopilotProfile("company1", ApiDialect.Anthropic);
        profile.AnthropicKey.ShouldBe("company1-github-anthropic");
        profile.OpenAiKey.ShouldBe("company1-github-openai");
        profile.Key.ShouldBe("company1-github-anthropic");
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("company1", "company1")]
    [InlineData("c", "c")]
    [InlineData("a-b-c", "a-b-c")]
    public void Accepts_valid_prefix_slugs(string? input, string expected)
    {
        GithubCopilotProfileKey.TryNormalizePrefix(input, out string normalized).ShouldBeTrue();
        normalized.ShouldBe(expected);
    }

    [Theory]
    [InlineData("-company")]
    [InlineData("company-")]
    [InlineData("Company")]
    [InlineData("company 1")]
    [InlineData("company_1")]
    [InlineData("comp--any")]
    public void Rejects_invalid_prefix_slugs(string input)
    {
        GithubCopilotProfileKey.TryNormalizePrefix(input, out _).ShouldBeFalse();
    }
}
