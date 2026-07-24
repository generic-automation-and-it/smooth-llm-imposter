using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Domain.UnitTest.Routing;

public class SessionForwardingPolicyTests
{
    private static RouteDecision Decision(bool isImposter, SessionForwarding forwarding) =>
        new(
            new ProviderRoute(
                "p", ApiDialect.OpenAi, new Uri("https://p.example"), null, false, null, [],
                SessionForwarding: forwarding),
            TargetModel: "target",
            CachingEnabled: false,
            IsImposter: isImposter);

    [Theory]
    // Opt-in requires BOTH a matched imposter route AND the OpencodeGo profile — the single gate
    // consulted by router, transformers, and forwarder (LADR-01). Any other combination stays off.
    [InlineData(true, SessionForwarding.OpencodeGo, true)]
    [InlineData(true, SessionForwarding.None, false)]
    [InlineData(false, SessionForwarding.OpencodeGo, false)]
    [InlineData(false, SessionForwarding.None, false)]
    public void IsOptedIn_is_true_only_for_matched_imposter_with_opencode_go(
        bool isImposter, SessionForwarding forwarding, bool expected)
    {
        SessionForwardingPolicy.IsOptedIn(Decision(isImposter, forwarding)).ShouldBe(expected);
    }
}
