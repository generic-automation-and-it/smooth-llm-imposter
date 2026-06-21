using Microsoft.Extensions.Options;
using SmoothLlmImposter.Application.Features.Routing;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.UnitTest.Routing;

public class ProviderCatalogTests
{
    private static ProviderRoute BuildOpenAi(string? upstreamApi, string? normalization)
    {
        var catalog = new ProviderCatalog(new StaticOptionsSnapshot<ImposterOptions>(new ImposterOptions
        {
            Providers =
            {
                ["p"] = new ProviderOptions
                {
                    Dialect = "openai", BaseUrl = "https://p.example",
                    OpenAiUpstreamApi = upstreamApi, RequestNormalization = normalization
                }
            }
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

    [Fact]
    public void Route_name_uses_key_when_name_unset()
    {
        var catalog = new ProviderCatalog(new StaticOptionsSnapshot<ImposterOptions>(new ImposterOptions
        {
            Providers = { ["opencode-go"] = new ProviderOptions { Dialect = "openai", BaseUrl = "https://o.example" } }
        }));

        catalog.ProvidersFor(ApiDialect.OpenAi).Single().Name.ShouldBe("opencode-go");
    }

    [Fact]
    public void Route_name_uses_explicit_name_override_over_key()
    {
        var catalog = new ProviderCatalog(new StaticOptionsSnapshot<ImposterOptions>(new ImposterOptions
        {
            Providers = { ["opencode-go"] = new ProviderOptions { Name = "display", Dialect = "openai", BaseUrl = "https://o.example" } }
        }));

        catalog.ProvidersFor(ApiDialect.OpenAi).Single().Name.ShouldBe("display");
    }

    [Fact]
    public void Binding_same_providers_in_two_key_orders_yields_identical_route_per_name()
    {
        // NFR-01: a provider's resolved route is order-independent — keying is by name, never position.
        static ProviderOptions P(string url, bool isDefault = false) =>
            new() { Dialect = "openai", BaseUrl = url, IsDefault = isDefault, AuthScheme = "Bearer" };

        var first = new ProviderCatalog(new StaticOptionsSnapshot<ImposterOptions>(new ImposterOptions
        {
            Providers = { ["a"] = P("https://a.example", isDefault: true), ["b"] = P("https://b.example") }
        }));
        var second = new ProviderCatalog(new StaticOptionsSnapshot<ImposterOptions>(new ImposterOptions
        {
            Providers = { ["b"] = P("https://b.example"), ["a"] = P("https://a.example", isDefault: true) }
        }));

        // Compare the scalar identity projection (record equality can't compare the Models array by value).
        static object Identity(ProviderRoute r) =>
            (r.Name, r.Dialect, r.BaseUrl, r.Secret, r.IsDefault, r.OpenAiUpstreamApi, r.AuthScheme, r.RequestNormalization);

        static ProviderRoute Named(ProviderCatalog c, string name) =>
            c.ProvidersFor(ApiDialect.OpenAi).Single(r => r.Name == name);

        Identity(Named(first, "a")).ShouldBe(Identity(Named(second, "a")));
        Identity(Named(first, "b")).ShouldBe(Identity(Named(second, "b")));
    }
}
