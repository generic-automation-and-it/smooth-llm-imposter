using Microsoft.Extensions.Options;
using SmoothLlmImposter.Application.Features.Routing;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.UnitTest.Routing;

public class RouteResolverTests
{
    private static RouteResolver Build(params ProviderOptions[] providers) =>
        new(new ProviderCatalog(Options.Create(new ImposterOptions { Providers = [.. providers] })));

    private static ProviderOptions OpenAi(string name, bool isDefault = false, params ModelMappingOptions[] models) =>
        new() { Name = name, Api = "openai", BaseUrl = "https://" + name + ".example", IsDefault = isDefault, Models = [.. models] };

    [Fact]
    public void Exact_mapping_is_an_imposter_route_with_rewritten_model()
    {
        RouteResolver resolver = Build(
            OpenAi("opencode", models: new ModelMappingOptions { From = "gpt5.4", To = "grok-code", Caching = true }),
            OpenAi("openai-official", isDefault: true));

        RouteDecision decision = resolver.Resolve(ApiDialect.OpenAi, "gpt5.4");

        decision.IsImposter.ShouldBeTrue();
        decision.Provider.Name.ShouldBe("opencode");
        decision.TargetModel.ShouldBe("grok-code");
        decision.CachingEnabled.ShouldBeTrue();
    }

    [Fact]
    public void Unmatched_model_falls_back_to_default_provider_unchanged()
    {
        RouteResolver resolver = Build(
            OpenAi("opencode", models: new ModelMappingOptions { From = "gpt5.4", To = "grok-code" }),
            OpenAi("openai-official", isDefault: true));

        RouteDecision decision = resolver.Resolve(ApiDialect.OpenAi, "gpt5.5");

        decision.IsImposter.ShouldBeFalse();
        decision.Provider.Name.ShouldBe("openai-official");
        decision.TargetModel.ShouldBe("gpt5.5");
        decision.CachingEnabled.ShouldBeFalse();
    }

    [Fact]
    public void First_matching_mapping_wins_in_configuration_order()
    {
        RouteResolver resolver = Build(
            OpenAi("first", models: new ModelMappingOptions { From = "gpt*", To = "a" }),
            OpenAi("second", models: new ModelMappingOptions { From = "gpt5.4", To = "b" }),
            OpenAi("default", isDefault: true));

        resolver.Resolve(ApiDialect.OpenAi, "gpt5.4").Provider.Name.ShouldBe("first");
    }

    [Fact]
    public void Unmatched_model_without_default_throws_404()
    {
        RouteResolver resolver = Build(
            OpenAi("opencode", models: new ModelMappingOptions { From = "gpt5.4", To = "grok-code" }));

        RoutingException ex = Should.Throw<RoutingException>(() => resolver.Resolve(ApiDialect.OpenAi, "gpt5.5"));
        ex.StatusCode.ShouldBe(404);
    }

    [Fact]
    public void Blank_model_throws()
    {
        RouteResolver resolver = Build(OpenAi("openai-official", isDefault: true));
        Should.Throw<RoutingException>(() => resolver.Resolve(ApiDialect.OpenAi, " "));
    }
}
