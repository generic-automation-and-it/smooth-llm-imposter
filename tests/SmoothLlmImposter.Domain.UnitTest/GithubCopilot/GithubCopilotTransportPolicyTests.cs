using SmoothLlmImposter.Domain.GithubCopilot;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Domain.UnitTest.GithubCopilot;

/// <summary>
/// L0 — the Copilot path/origin policy. These are the checks that keep a managed GitHub token from leaving for
/// an arbitrary origin or a path the profile does not serve.
/// </summary>
public class GithubCopilotTransportPolicyTests
{
    [Theory]
    [InlineData(ApiDialect.Anthropic, "/v1/messages", "/v1/messages")]
    [InlineData(ApiDialect.OpenAi, "/responses", "/responses")]
    [InlineData(ApiDialect.OpenAi, "/v1/responses", "/responses")]
    public void Maps_the_supported_same_dialect_paths(ApiDialect dialect, string inbound, string expected)
    {
        GithubCopilotTransportPolicy.TryResolveUpstreamPath(dialect, inbound, out string upstreamPath).ShouldBeTrue();
        upstreamPath.ShouldBe(expected);
    }

    [Theory]
    [InlineData(ApiDialect.Anthropic, "/v1/chat/completions")]
    [InlineData(ApiDialect.Anthropic, "/responses")]
    [InlineData(ApiDialect.OpenAi, "/v1/chat/completions")]
    [InlineData(ApiDialect.OpenAi, "/chat/completions")]
    [InlineData(ApiDialect.OpenAi, "/v1/models")]
    [InlineData(ApiDialect.OpenAi, "/v1/messages")]
    public void Rejects_chat_and_every_other_path(ApiDialect dialect, string inbound)
    {
        GithubCopilotTransportPolicy.TryResolveUpstreamPath(dialect, inbound, out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("https://api.githubcopilot.com")]
    [InlineData("https://api.enterprise.githubcopilot.com")]
    [InlineData("https://githubcopilot.com")]
    [InlineData("https://api.githubcopilot.com/")]
    public void Accepts_github_controlled_https_origins(string origin)
    {
        GithubCopilotTransportPolicy.IsAllowedOrigin(origin).ShouldBeTrue();
    }

    [Theory]
    [InlineData("http://api.githubcopilot.com")]
    [InlineData("https://api.githubcopilot.com:8443")]
    [InlineData("https://api.githubcopilot.com/path")]
    [InlineData("https://api.githubcopilot.com?query=1")]
    [InlineData("https://user:pass@api.githubcopilot.com")]
    [InlineData("https://githubcopilot.com.evil.test")]
    [InlineData("https://evilsuffixgithubcopilot.com")]
    [InlineData("https://api.githubcopilot.com.")]
    [InlineData("https://localhost")]
    [InlineData("https://10.0.0.1")]
    [InlineData("https://192.168.1.1")]
    [InlineData("https://example.com")]
    [InlineData("not-a-url")]
    [InlineData(null)]
    public void Rejects_lookalikes_private_destinations_and_malformed_origins(string? origin)
    {
        GithubCopilotTransportPolicy.IsAllowedOrigin(origin).ShouldBeFalse();
    }
}
