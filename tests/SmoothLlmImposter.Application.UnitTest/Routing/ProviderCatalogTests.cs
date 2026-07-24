using Microsoft.Extensions.Options;
using SmoothLlmImposter.Application.Features.Routing;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.UnitTest.Routing;

public class ProviderCatalogTests
{
    private static ProviderRoute BuildOpenAi(string? upstreamApi, string? normalization)
    {
        ProviderCatalog catalog = ProviderCatalogTestFactory.SeededCatalog(new Dictionary<string, ProviderOptions>(StringComparer.Ordinal)
        {
            ["p"] = new()
            {
                Dialect = "openai",
                BaseUrl = "https://p.example",
                OpenAiUpstreamApi = upstreamApi,
                RequestNormalization = normalization
            }
        });

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
    public void Auth_header_flows_to_the_route_and_blank_is_normalized_to_null()
    {
        ProviderCatalog catalog = ProviderCatalogTestFactory.SeededCatalog(new Dictionary<string, ProviderOptions>(StringComparer.Ordinal)
        {
            ["set"] = new() { Dialect = "openai", BaseUrl = "https://s.example", AuthHeader = "api-key" },
            ["blank"] = new() { Dialect = "openai", BaseUrl = "https://b.example", AuthHeader = "   " }
        });

        IReadOnlyList<ProviderRoute> routes = catalog.ProvidersFor(ApiDialect.OpenAi);
        routes.Single(r => r.Name == "set").AuthHeader.ShouldBe("api-key");
        routes.Single(r => r.Name == "blank").AuthHeader.ShouldBeNull();
    }

    [Fact]
    public void Route_name_uses_key_when_name_unset()
    {
        ProviderCatalog catalog = ProviderCatalogTestFactory.SeededCatalog(new Dictionary<string, ProviderOptions>(StringComparer.Ordinal)
        {
            ["opencode-go"] = new() { Dialect = "openai", BaseUrl = "https://o.example" }
        });

        catalog.ProvidersFor(ApiDialect.OpenAi).Single().Name.ShouldBe("opencode-go");
    }

    [Fact]
    public void Route_name_uses_explicit_name_override_over_key()
    {
        ProviderCatalog catalog = ProviderCatalogTestFactory.SeededCatalog(new Dictionary<string, ProviderOptions>(StringComparer.Ordinal)
        {
            ["opencode-go"] = new() { Name = "display", Dialect = "openai", BaseUrl = "https://o.example" }
        });

        ProviderRoute route = catalog.ProvidersFor(ApiDialect.OpenAi).Single();
        route.Name.ShouldBe("display");
        route.CredentialProviderName.ShouldBe("opencode-go");
    }

    [Fact]
    public void Seeded_registry_is_the_catalog_source_without_touching_options_value()
    {
        var registry = new InMemoryProviderRegistry();
        registry.Seed(new Dictionary<string, ProviderOptions>(StringComparer.Ordinal)
        {
            ["runtime"] = new() { Dialect = "openai", BaseUrl = "https://runtime.example" }
        });

        var catalog = new ProviderCatalog(registry, new ThrowingOptions());

        catalog.ProvidersFor(ApiDialect.OpenAi).Single().BaseUrl.ShouldBe(new Uri("https://runtime.example"));
    }

    [Fact]
    public void Unseeded_registry_falls_back_to_cached_options()
    {
        ProviderCatalog catalog = ProviderCatalogTestFactory.UnseededCatalog(new ImposterOptions
        {
            Providers =
            {
                ["bound"] = new ProviderOptions { Dialect = "openai", BaseUrl = "https://bound.example" }
            }
        });

        catalog.ProvidersFor(ApiDialect.OpenAi).Single().BaseUrl.ShouldBe(new Uri("https://bound.example"));
    }

    [Fact]
    public void Binding_same_providers_in_two_key_orders_yields_identical_route_per_name()
    {
        // NFR-01: a provider's resolved route is order-independent — keying is by name, never position.
        static ProviderOptions P(string url, bool isDefault = false) =>
            new() { Dialect = "openai", BaseUrl = url, IsDefault = isDefault, AuthScheme = "Bearer" };

        ProviderCatalog first = ProviderCatalogTestFactory.SeededCatalog(new Dictionary<string, ProviderOptions>(StringComparer.Ordinal)
        {
            ["a"] = P("https://a.example", isDefault: true),
            ["b"] = P("https://b.example")
        });
        ProviderCatalog second = ProviderCatalogTestFactory.SeededCatalog(new Dictionary<string, ProviderOptions>(StringComparer.Ordinal)
        {
            ["b"] = P("https://b.example"),
            ["a"] = P("https://a.example", isDefault: true)
        });

        // Compare the scalar identity projection (record equality can't compare the Models array by value).
        static object Identity(ProviderRoute r) =>
            (r.Name, r.Dialect, r.BaseUrl, r.Secret, r.IsDefault, r.OpenAiUpstreamApi, r.AuthScheme, r.RequestNormalization);

        static ProviderRoute Named(ProviderCatalog c, string name) =>
            c.ProvidersFor(ApiDialect.OpenAi).Single(r => r.Name == name);

        Identity(Named(first, "a")).ShouldBe(Identity(Named(second, "a")));
        Identity(Named(first, "b")).ShouldBe(Identity(Named(second, "b")));
    }

    private sealed class ThrowingOptions : IOptions<ImposterOptions>
    {
        public ImposterOptions Value =>
            throw new InvalidOperationException("Options value should not be evaluated when the registry is seeded.");
    }

    [Fact]
    public void Session_forwarding_flows_to_the_route()
    {
        ProviderCatalog catalog = ProviderCatalogTestFactory.SeededCatalog(new Dictionary<string, ProviderOptions>(StringComparer.Ordinal)
        {
            ["set"] = new() { Dialect = "openai", BaseUrl = "https://s.example", SessionForwarding = "opencode-go" },
            ["blank"] = new() { Dialect = "openai", BaseUrl = "https://b.example", SessionForwarding = null }
        });

        IReadOnlyList<ProviderRoute> routes = catalog.ProvidersFor(ApiDialect.OpenAi);
        routes.Single(r => r.Name == "set").SessionForwarding.ShouldBe(SessionForwarding.OpencodeGo);
        routes.Single(r => r.Name == "blank").SessionForwarding.ShouldBe(SessionForwarding.None);
    }
}
