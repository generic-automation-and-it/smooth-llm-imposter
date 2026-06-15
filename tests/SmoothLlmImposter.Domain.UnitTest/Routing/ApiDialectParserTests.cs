using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Domain.UnitTest.Routing;

public class ApiDialectParserTests
{
    [Theory]
    [InlineData("openai", ApiDialect.OpenAi)]
    [InlineData("OpenAI", ApiDialect.OpenAi)]
    [InlineData(" anthropic ", ApiDialect.Anthropic)]
    public void Parses_known_dialects(string value, ApiDialect expected) =>
        ApiDialectParser.Parse(value).ShouldBe(expected);

    [Theory]
    [InlineData("gemini")]
    [InlineData("")]
    [InlineData(null)]
    public void Throws_on_unknown_dialect(string? value) =>
        Should.Throw<ArgumentException>(() => ApiDialectParser.Parse(value));

    [Fact]
    public void TryParse_returns_false_for_unknown() =>
        ApiDialectParser.TryParse("nope", out _).ShouldBeFalse();
}
