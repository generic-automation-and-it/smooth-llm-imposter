using SmoothLlmImposter.Application.Features.Routing;

namespace SmoothLlmImposter.Application.UnitTest.Routing;

public class ProviderRegistryOptionsPostConfigureTests
{
    private static ProviderOptions Provider(string baseUrl) =>
        new() { Dialect = "openai", BaseUrl = baseUrl };

    [Fact]
    public void Unseeded_registry_leaves_the_bound_options_untouched()
    {
        // Before the seeder runs (e.g. during ValidateOnStart) the overlay must be a no-op so the env-applied
        // baseline is what gets validated (HLD 008).
        var registry = new InMemoryProviderRegistry();
        var sut = new ProviderRegistryOptionsPostConfigure(registry);
        var options = new ImposterOptions { Providers = { ["bound"] = Provider("https://bound.example") } };

        sut.PostConfigure(null, options);

        options.Providers.Keys.ShouldBe(["bound"]);
        options.Providers["bound"].BaseUrl.ShouldBe("https://bound.example");
    }

    [Fact]
    public void Seeded_registry_clears_and_overlays_its_snapshot()
    {
        var registry = new InMemoryProviderRegistry();
        registry.Seed(new Dictionary<string, ProviderOptions>(StringComparer.Ordinal)
        {
            ["runtime"] = Provider("https://runtime.example")
        });
        var sut = new ProviderRegistryOptionsPostConfigure(registry);
        var options = new ImposterOptions { Providers = { ["bound"] = Provider("https://bound.example") } };

        sut.PostConfigure(null, options);

        // The bound provider is cleared and replaced by the registry snapshot — runtime CRUD wins (LADR-04).
        options.Providers.Keys.ShouldBe(["runtime"]);
        options.Providers.ContainsKey("bound").ShouldBeFalse();
        options.Providers["runtime"].BaseUrl.ShouldBe("https://runtime.example");
    }
}
