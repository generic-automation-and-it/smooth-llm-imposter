using System.Text.Json.Nodes;
using SmoothLlmImposter.Application.Features.Routing;

namespace SmoothLlmImposter.Application.UnitTest.Routing;

public class OpenAiModelCatalogResponderTests
{
    private static OpenAiModelCatalogResponder Build(params ProviderOptions[] providers) =>
        new(new ProviderCatalog(new StaticOptionsSnapshot<ImposterOptions>(new ImposterOptions
        {
            Providers = providers.ToDictionary(static p => p.Name!, StringComparer.Ordinal)
        })));

    private static ProviderOptions OpenAi(string name, string? secret = null, bool isDefault = false, params ModelMappingOptions[] models) =>
        new() { Name = name, Dialect = "openai", BaseUrl = "https://" + name + ".example", Secret = secret, IsDefault = isDefault, Models = [.. models] };

    private static ProviderOptions Anthropic(string name, bool isDefault = false, params ModelMappingOptions[] models) =>
        new() { Name = name, Dialect = "anthropic", BaseUrl = "https://" + name + ".example", IsDefault = isDefault, Models = [.. models] };

    private static ModelMappingOptions Map(string from, string to) => new() { From = from, To = to };

    [Fact]
    public void Aggregates_distinct_to_values_in_catalogue_order_with_first_declaring_owner()
    {
        OpenAiModelCatalogResponder responder = Build(
            OpenAi("openai-official", isDefault: true),
            OpenAi("opencode", models: [Map("gpt5.4", "grok-code"), Map("gpt-shared", "shared-model")]),
            OpenAi("openrouter", models: [Map("gpt-shared-2", "shared-model"), Map("gpt-z", "another-model")]));

        JsonObject root = JsonNode.Parse(responder.BuildOpenAiModelsResponse())!.AsObject();
        JsonArray data = root["data"]!.AsArray();

        // Distinct `to` set, in catalogue order; the duplicate "shared-model" collapses to one entry.
        data.Select(m => m!["id"]!.GetValue<string>())
            .ShouldBe(["grok-code", "shared-model", "another-model"]);

        // First declaring provider (opencode) supplies owned_by for the duplicated target.
        data.Single(m => m!["id"]!.GetValue<string>() == "shared-model")!["owned_by"]!
            .GetValue<string>().ShouldBe("opencode");
        data.Single(m => m!["id"]!.GetValue<string>() == "another-model")!["owned_by"]!
            .GetValue<string>().ShouldBe("openrouter");
    }

    [Fact]
    public void Envelope_is_a_list_and_every_entry_carries_required_model_fields()
    {
        OpenAiModelCatalogResponder responder = Build(
            OpenAi("opencode", models: [Map("gpt5.4", "grok-code")]));

        JsonObject root = JsonNode.Parse(responder.BuildOpenAiModelsResponse())!.AsObject();

        root["object"]!.GetValue<string>().ShouldBe("list");
        foreach (JsonNode? entry in root["data"]!.AsArray())
        {
            entry!["id"]!.GetValue<string>().ShouldNotBeNullOrEmpty();
            entry["object"]!.GetValue<string>().ShouldBe("model");
            entry["created"]!.GetValue<long>().ShouldBe(0);
            entry["owned_by"]!.GetValue<string>().ShouldNotBeNullOrEmpty();
        }
    }

    [Fact]
    public void Empty_catalogue_returns_object_list_with_empty_data()
    {
        OpenAiModelCatalogResponder responder = Build();

        JsonObject root = JsonNode.Parse(responder.BuildOpenAiModelsResponse())!.AsObject();

        root["object"]!.GetValue<string>().ShouldBe("list");
        root["data"]!.AsArray().Count.ShouldBe(0);
    }

    [Fact]
    public void Default_and_passthrough_providers_without_mappings_contribute_nothing()
    {
        OpenAiModelCatalogResponder responder = Build(
            OpenAi("openai-official", isDefault: true),
            Anthropic("anthropic-official", isDefault: true, models: Map("claude-haiku-*", "claude-3-5-haiku-latest")));

        JsonObject root = JsonNode.Parse(responder.BuildOpenAiModelsResponse())!.AsObject();

        // No OpenAI mappings → empty data. The Anthropic mapping is a different dialect and is never aggregated here.
        root["data"]!.AsArray().Count.ShouldBe(0);
    }

    [Fact]
    public void Created_is_a_fixed_constant_for_every_entry()
    {
        OpenAiModelCatalogResponder responder = Build(
            OpenAi("opencode", models: [Map("a", "model-a"), Map("b", "model-b")]));

        JsonArray data = JsonNode.Parse(responder.BuildOpenAiModelsResponse())!.AsObject()["data"]!.AsArray();

        data.ShouldAllBe(m => m!["created"]!.GetValue<long>() == 0);
    }

    [Fact]
    public void Two_calls_under_identical_config_are_byte_identical()
    {
        OpenAiModelCatalogResponder responder = Build(
            OpenAi("opencode", models: [Map("gpt5.4", "grok-code"), Map("gpt-shared", "shared-model")]),
            OpenAi("openrouter", models: [Map("gpt-z", "another-model")]));

        responder.BuildOpenAiModelsResponse().ShouldBe(responder.BuildOpenAiModelsResponse());
    }

    [Fact]
    public void Response_never_contains_a_provider_secret()
    {
        OpenAiModelCatalogResponder responder = Build(
            OpenAi("opencode", secret: "sk-super-secret-key", models: [Map("gpt5.4", "grok-code")]));

        responder.BuildOpenAiModelsResponse().ShouldNotContain("sk-super-secret-key");
    }

    [Fact]
    public void Disabled_providers_are_excluded_from_the_models_catalogue()
    {
        ProviderOptions disabled = OpenAi("disabled", models: [Map("gpt5.4", "hidden-model")]);
        disabled.Enabled = false;

        OpenAiModelCatalogResponder responder = Build(
            disabled,
            OpenAi("enabled", models: [Map("gpt5.5", "visible-model")]));

        string[] ids = [.. JsonNode.Parse(responder.BuildOpenAiModelsResponse())!.AsObject()["data"]!
            .AsArray().Select(m => m!["id"]!.GetValue<string>())];

        ids.ShouldContain("visible-model");
        ids.ShouldNotContain("hidden-model");
    }
}
