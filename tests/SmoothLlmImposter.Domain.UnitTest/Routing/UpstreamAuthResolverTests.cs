using SmoothLlmImposter.Domain.Credentials;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Domain.UnitTest.Routing;

public class UpstreamAuthResolverTests
{
    [Theory]
    [InlineData(CredentialAuthScheme.Bearer, "Authorization")]
    [InlineData(CredentialAuthScheme.ApiKey, "x-api-key")]
    public void Default_header_name_matches_the_scheme(CredentialAuthScheme scheme, string expected) =>
        UpstreamAuthResolver.DefaultHeaderNameFor(scheme).ShouldBe(expected);

    [Theory]
    [InlineData(ApiDialect.Anthropic, CredentialAuthScheme.ApiKey)]
    [InlineData(ApiDialect.OpenAi, CredentialAuthScheme.Bearer)]
    public void Default_scheme_follows_dialect(ApiDialect dialect, CredentialAuthScheme expected) =>
        UpstreamAuthResolver.DefaultSchemeFor(dialect).ShouldBe(expected);
}
