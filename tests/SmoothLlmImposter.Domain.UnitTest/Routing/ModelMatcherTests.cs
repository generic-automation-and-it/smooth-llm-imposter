using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Domain.UnitTest.Routing;

public class ModelMatcherTests
{
    [Theory]
    [InlineData("gpt5.4", "gpt5.4", true)]
    [InlineData("gpt5.4", "GPT5.4", true)]
    [InlineData("gpt5.4", "gpt5.5", false)]
    [InlineData("gpt5.4", "gpt5.40", false)]
    public void Exact_matches_are_case_insensitive(string pattern, string model, bool expected) =>
        ModelMatcher.Matches(pattern, model).ShouldBe(expected);

    [Theory]
    [InlineData("claude-haiku-*", "claude-haiku-20241022", true)]
    [InlineData("claude-haiku-*", "claude-haiku-", true)]
    [InlineData("claude-haiku-*", "CLAUDE-HAIKU-x", true)]
    [InlineData("claude-haiku-*", "claude-sonnet-x", false)]
    public void Trailing_wildcard_matches_prefix(string pattern, string model, bool expected) =>
        ModelMatcher.Matches(pattern, model).ShouldBe(expected);

    [Fact]
    public void Empty_pattern_never_matches() =>
        ModelMatcher.Matches(string.Empty, "anything").ShouldBeFalse();
}
