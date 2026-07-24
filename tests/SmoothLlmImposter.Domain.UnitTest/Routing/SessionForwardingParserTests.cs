using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Domain.UnitTest.Routing;

public class SessionForwardingParserTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("none")]
    [InlineData("None")]
    [InlineData("NONE")]
    [InlineData(" none ")]
    public void TryParse_returns_None_for_blank_or_none(string? value)
    {
        SessionForwardingParser.TryParse(value, out SessionForwarding forwarding).ShouldBeTrue();
        forwarding.ShouldBe(SessionForwarding.None);
    }

    [Theory]
    [InlineData("opencode-go")]
    [InlineData("opencode_go")]
    [InlineData("opencodego")]
    [InlineData("OpenCode-Go")]
    [InlineData("OPENCODE-GO")]
    [InlineData(" OpenCode_Go ")]
    public void TryParse_returns_OpencodeGo_for_any_accepted_spelling(string value)
    {
        SessionForwardingParser.TryParse(value, out SessionForwarding forwarding).ShouldBeTrue();
        forwarding.ShouldBe(SessionForwarding.OpencodeGo);
    }

    [Theory]
    [InlineData("sticky")]
    [InlineData("session_id")]
    [InlineData("opencode")]
    [InlineData("-opencode-go")]
    public void TryParse_returns_false_for_unrecognised_present_value(string value)
    {
        // A present-but-unrecognised value is reported as invalid (try-parse contract); blank input is
        // a separate case documented as "valid + None" — see SessionForwardingParser.TryParse XML doc.
        SessionForwardingParser.TryParse(value, out _).ShouldBeFalse();
    }

    [Fact]
    public void Parse_returns_None_for_blank_input()
    {
        SessionForwardingParser.Parse(null).ShouldBe(SessionForwarding.None);
        SessionForwardingParser.Parse("").ShouldBe(SessionForwarding.None);
        SessionForwardingParser.Parse("   ").ShouldBe(SessionForwarding.None);
    }

    [Fact]
    public void Parse_throws_for_unrecognised_value()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => SessionForwardingParser.Parse("sticky"));
    }
}
