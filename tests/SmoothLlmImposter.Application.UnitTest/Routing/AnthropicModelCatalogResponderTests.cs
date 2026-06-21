using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using SmoothLlmImposter.Application.Features.Routing;

namespace SmoothLlmImposter.Application.UnitTest.Routing;

public class AnthropicModelCatalogResponderTests
{
    private static AnthropicModelCatalogResponder Build(params ProviderOptions[] providers) =>
        new(new ProviderCatalog(Options.Create(new ImposterOptions
        {
            Providers = providers.ToDictionary(static p => p.Name!, StringComparer.Ordinal)
        })));

    private static ProviderOptions Anthropic(string name, bool isDefault = false, string? secret = null, params ModelMappingOptions[] models) =>
        new() { Name = name, Dialect = "anthropic", BaseUrl = "https://" + name + ".example", IsDefault = isDefault, Secret = secret, Models = [.. models] };

    private static ProviderOptions OpenAi(string name, params ModelMappingOptions[] models) =>
        new() { Name = name, Dialect = "openai", BaseUrl = "https://" + name + ".example", Models = [.. models] };

    private static ModelMappingOptions Map(string from, string to) => new() { From = from, To = to };

    private static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();

    private static string[] Ids(JsonObject envelope) =>
        [.. envelope["data"]!.AsArray().Select(m => m!["id"]!.GetValue<string>())];

    [Fact]
    public void Aggregates_distinct_union_of_to_targets_in_catalogue_order()
    {
        AnthropicModelCatalogResponder responder = Build(
            Anthropic("anthropic-official", isDefault: true),
            Anthropic("imposter-a", models: [Map("alias-x", "claude-sonnet-4-6"), Map("alias-y", "claude-opus-4-8")]),
            Anthropic("imposter-b", models: [Map("alias-z", "claude-sonnet-4-6"), Map("alias-w", "claude-haiku-4-5")]));

        string[] ids = Ids(Parse(responder.BuildModelsResponse()));

        // claude-sonnet-4-6 declared under both imposter-a and imposter-b collapses to one entry, in first-seen order.
        ids.ShouldBe(["claude-sonnet-4-6", "claude-opus-4-8", "claude-haiku-4-5"]);
    }

    [Fact]
    public void Default_passthrough_providers_and_other_dialects_contribute_nothing()
    {
        AnthropicModelCatalogResponder responder = Build(
            Anthropic("anthropic-official", isDefault: true),                                  // no Models[] → nothing
            OpenAi("openai-imposter", Map("gpt", "grok-code")),                                 // OpenAI dialect → excluded
            Anthropic("imposter-a", models: [Map("alias-x", "claude-sonnet-4-6")]));

        string[] ids = Ids(Parse(responder.BuildModelsResponse()));

        ids.ShouldBe(["claude-sonnet-4-6"]);
        ids.ShouldNotContain("grok-code");
    }

    [Fact]
    public void Empty_anthropic_catalogue_returns_valid_envelope_with_empty_data()
    {
        AnthropicModelCatalogResponder responder = Build(OpenAi("openai-only", Map("gpt", "grok-code")));

        JsonObject envelope = Parse(responder.BuildModelsResponse());

        envelope["data"]!.AsArray().Count.ShouldBe(0);
        envelope["has_more"]!.GetValue<bool>().ShouldBeFalse();
        envelope["first_id"].ShouldBeNull(); // JSON null
        envelope["last_id"].ShouldBeNull();
        envelope.ContainsKey("data").ShouldBeTrue();
        envelope.ContainsKey("first_id").ShouldBeTrue();
        envelope.ContainsKey("last_id").ShouldBeTrue();
    }

    [Fact]
    public void Envelope_uses_anthropic_shape_not_openai()
    {
        AnthropicModelCatalogResponder responder = Build(
            Anthropic("imposter-a", models: [Map("alias-x", "claude-sonnet-4-6")]));

        JsonObject envelope = Parse(responder.BuildModelsResponse());

        // Anthropic envelope has NO object:"list" and carries pagination fields.
        envelope.ContainsKey("object").ShouldBeFalse();
        envelope.ContainsKey("data").ShouldBeTrue();
        envelope.ContainsKey("first_id").ShouldBeTrue();
        envelope.ContainsKey("has_more").ShouldBeTrue();
        envelope.ContainsKey("last_id").ShouldBeTrue();

        JsonObject model = envelope["data"]!.AsArray()[0]!.AsObject();
        model["id"]!.GetValue<string>().ShouldBe("claude-sonnet-4-6");
        model["type"]!.GetValue<string>().ShouldBe("model"); // not object:"model"
        model.ContainsKey("object").ShouldBeFalse();
        model["display_name"]!.GetValue<string>().ShouldBe("claude-sonnet-4-6"); // to id verbatim
    }

    [Fact]
    public void First_and_last_id_track_the_data_bounds()
    {
        AnthropicModelCatalogResponder responder = Build(
            Anthropic("imposter-a", models: [Map("a", "claude-sonnet-4-6"), Map("b", "claude-opus-4-8"), Map("c", "claude-haiku-4-5")]));

        JsonObject envelope = Parse(responder.BuildModelsResponse());

        envelope["first_id"]!.GetValue<string>().ShouldBe("claude-sonnet-4-6");
        envelope["last_id"]!.GetValue<string>().ShouldBe("claude-haiku-4-5");
        envelope["has_more"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public void Created_at_is_a_fixed_constant_on_every_entry()
    {
        AnthropicModelCatalogResponder responder = Build(
            Anthropic("imposter-a", models: [Map("a", "claude-sonnet-4-6"), Map("b", "claude-opus-4-8")]));

        JsonArray data = Parse(responder.BuildModelsResponse())["data"]!.AsArray();

        foreach (JsonNode? model in data)
        {
            model!["created_at"]!.GetValue<string>().ShouldBe("1970-01-01T00:00:00Z");
        }
    }

    [Fact]
    public void Two_calls_under_identical_config_are_byte_identical()
    {
        AnthropicModelCatalogResponder responder = Build(
            Anthropic("imposter-a", models: [Map("a", "claude-sonnet-4-6"), Map("b", "claude-opus-4-8")]),
            Anthropic("imposter-b", models: [Map("c", "claude-haiku-4-5")]));

        responder.BuildModelsResponse().ShouldBe(responder.BuildModelsResponse());
    }

    [Fact]
    public void Response_never_contains_a_provider_secret()
    {
        AnthropicModelCatalogResponder responder = Build(
            Anthropic("imposter-a", secret: "sk-ant-super-secret", models: [Map("alias-x", "claude-sonnet-4-6")]));

        responder.BuildModelsResponse().ShouldNotContain("sk-ant-super-secret");
    }
}
