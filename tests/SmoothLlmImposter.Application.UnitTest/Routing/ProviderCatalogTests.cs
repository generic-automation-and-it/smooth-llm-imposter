using Microsoft.Extensions.Options;
using SmoothLlmImposter.Application.Features.Routing;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.UnitTest.Routing;

public class ProviderCatalogTests
{
    private static ProviderRoute BuildOpenAi(string? upstreamApi, string? normalization)
    {
        var catalog = new ProviderCatalog(Options.Create(new ImposterOptions
        {
            Providers =
            [
                new ProviderOptions
                {
                    Name = "p", Dialect = "openai", BaseUrl = "https://p.example",
                    OpenAiUpstreamApi = upstreamApi, RequestNormalization = normalization
                }
            ]
        }));

        return catalog.ProvidersFor(ApiDialect.OpenAi).Single();
    }

    [Fact]
    public void Chat_completions_defaults_normalization_on_when_unset() =>
        BuildOpenAi("chat_completions", normalization: null).RequestNormalization
            .ShouldBe(RequestNormalization.CodexToOpenAiSdk);

    [Fact]
    public void Chat_completions_can_opt_out_with_explicit_none() =>
        BuildOpenAi("chat_completions", normalization: "none").RequestNormalization
            .ShouldBe(RequestNormalization.None);

    [Fact]
    public void Responses_upstream_leaves_normalization_off_when_unset() =>
        BuildOpenAi(upstreamApi: null, normalization: null).RequestNormalization
            .ShouldBe(RequestNormalization.None);
}
