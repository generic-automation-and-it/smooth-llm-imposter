using SmoothLlmImposter.Application.Features.Routing;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.UnitTest.Routing;

public class RouteResolverTests
{
    private static RouteResolver Build(params ProviderOptions[] providers) =>
        new(new ProviderCatalog(new StaticOptionsSnapshot<ImposterOptions>(new ImposterOptions
        {
            Providers = providers.ToDictionary(static p => p.Name!, StringComparer.Ordinal)
        })));

    private static ProviderOptions OpenAi(string name, bool isDefault = false, params ModelMappingOptions[] models) =>
        new() { Name = name, Dialect = "openai", BaseUrl = "https://" + name + ".example", IsDefault = isDefault, Models = [.. models] };

    private static ProviderOptions Anthropic(string name, bool isDefault = false, params ModelMappingOptions[] models) =>
        new() { Name = name, Dialect = "anthropic", BaseUrl = "https://" + name + ".example", IsDefault = isDefault, Models = [.. models] };

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
    public void Model_token_in_To_expands_to_the_inbound_model_preserving_the_suffix()
    {
        // Prefix rewrite via the {model} template: the gateway wants "anthropic."-prefixed ids while
        // keeping whatever version suffix the caller sent (claude-opus-4-1 → anthropic.claude-opus-4-1).
        RouteResolver resolver = Build(
            Anthropic("anthropic", models: new ModelMappingOptions { From = "claude-opus-*", To = "anthropic.{model}" }),
            Anthropic("anthropic-default", isDefault: true));

        RouteDecision decision = resolver.Resolve(ApiDialect.Anthropic, "claude-opus-4-1-20250805");

        decision.IsImposter.ShouldBeTrue();
        decision.Provider.Name.ShouldBe("anthropic");
        decision.TargetModel.ShouldBe("anthropic.claude-opus-4-1-20250805");
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
    public void Disabled_provider_is_skipped_for_imposter_matching()
    {
        ProviderOptions disabled = OpenAi("disabled", models: new ModelMappingOptions { From = "gpt5.4", To = "disabled-model" });
        disabled.Enabled = false;

        RouteResolver resolver = Build(
            disabled,
            OpenAi("enabled", models: new ModelMappingOptions { From = "gpt5.4", To = "enabled-model" }),
            OpenAi("default", isDefault: true));

        RouteDecision decision = resolver.Resolve(ApiDialect.OpenAi, "gpt5.4");

        decision.Provider.Name.ShouldBe("enabled");
        decision.TargetModel.ShouldBe("enabled-model");
    }

    [Fact]
    public void Disabled_default_is_skipped_for_passthrough()
    {
        ProviderOptions disabledDefault = OpenAi("disabled-default", isDefault: true);
        disabledDefault.Enabled = false;

        RouteResolver resolver = Build(
            disabledDefault,
            OpenAi("enabled-default", isDefault: true));

        RouteDecision decision = resolver.Resolve(ApiDialect.OpenAi, "gpt5.5");

        decision.Provider.Name.ShouldBe("enabled-default");
    }

    [Fact]
    public void Blank_model_throws()
    {
        RouteResolver resolver = Build(OpenAi("openai-official", isDefault: true));
        Should.Throw<RoutingException>(() => resolver.Resolve(ApiDialect.OpenAi, " "));
    }

    [Fact]
    public void Personal_provider_captures_real_opus_id_with_canonical_glob()
    {
        // Guards the LADR-04 From-glob decision: the canonical "claude-opus-4-7*" matches a real inbound
        // Opus id (claude-opus-4-7-20250930), routing it to the operator's personal subscription and
        // pinning it to their chosen Opus version (To = claude-opus-4-8 — capture within the Opus family,
        // not a cross-vendor remap).
        RouteResolver resolver = Build(
            Anthropic("anthropic-personal", models: new ModelMappingOptions { From = "claude-opus-4-7*", To = "claude-opus-4-8", Caching = true }),
            Anthropic("anthropic-default", isDefault: true));

        RouteDecision decision = resolver.Resolve(ApiDialect.Anthropic, "claude-opus-4-7-20250930");

        decision.IsImposter.ShouldBeTrue();
        decision.Provider.Name.ShouldBe("anthropic-personal");
        decision.TargetModel.ShouldBe("claude-opus-4-8");
        decision.CachingEnabled.ShouldBeTrue();

        // The shorthand the worktask flagged (opus-4.7*) would not have matched this inbound id, which is
        // why the canonical claude-opus-4-7* glob is the correct From.
        ModelMatcher.Matches("opus-4.7*", "claude-opus-4-7-20250930").ShouldBeFalse();
    }
}
