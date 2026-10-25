using Microsoft.Extensions.Options;
using Shouldly;
using SmoothLlmImposter.Application.Features.Routing;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.UnitTest.Routing.Bridge;

public class AnthropicChatProviderOptionsTests
{
    private readonly ImposterOptionsValidator _validator = new();

    private static ImposterOptions Options(params (string Key, ProviderOptions Provider)[] entries) =>
        new() { Providers = entries.ToDictionary(static e => e.Key, static e => e.Provider, StringComparer.Ordinal) };

    private static ProviderOptions Bridged(string? authScheme = "Bearer") => new()
    {
        Dialect = "anthropic",
        BaseUrl = "https://chat.example",
        AnthropicUpstreamApi = "chat_completions",
        AuthScheme = authScheme,
        Models = [new ModelMappingOptions { From = "claude-*", To = "chat-model" }]
    };

    private static void ShouldFail(ProviderOptions provider, string fragment)
    {
        ValidateOptionsResult result = new ImposterOptionsValidator().Validate(null, Options(("chat", provider)));
        result.Succeeded.ShouldBeFalse();
        result.Failures!.ShouldContain(failure => failure.Contains(fragment, StringComparison.Ordinal));
    }

    [Fact]
    public void Bridged_bearer_provider_is_valid()
    {
        _validator.Validate(null, Options(("chat", Bridged()))).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Unknown_upstream_api_fails()
    {
        ProviderOptions provider = Bridged();
        provider.AnthropicUpstreamApi = "grpc";
        ShouldFail(provider, "AnthropicUpstreamApi 'grpc' is invalid");
    }

    [Fact]
    public void Chat_completions_requires_bearer_even_by_dialect_default()
    {
        ShouldFail(Bridged(authScheme: null), "requires an effective AuthScheme of 'Bearer'");

        ProviderOptions apiKey = Bridged();
        apiKey.AuthScheme = "ApiKey";
        ShouldFail(apiKey, "requires an effective AuthScheme of 'Bearer'");
    }

    [Fact]
    public void Chat_completions_is_anthropic_only()
    {
        ProviderOptions provider = Bridged();
        provider.Dialect = "openai";
        ShouldFail(provider, "only valid on the 'anthropic' dialect");
    }

    [Theory]
    [InlineData("CompatProfile", "foo", "CompatProfile 'foo' is invalid")]
    [InlineData("UserIdMode", "foo", "UserIdMode 'foo' is invalid")]
    [InlineData("MaxTokensField", "foo", "MaxTokensField 'foo' is invalid")]
    [InlineData("DocumentMode", "foo", "DocumentMode 'foo' is invalid")]
    [InlineData("MidturnSystem", "foo", "MidturnSystem 'foo' is invalid")]
    public void Unknown_bridge_option_fails(string field, string value, string fragment)
    {
        ProviderOptions provider = Bridged();
        switch (field)
        {
            case "CompatProfile": provider.CompatProfile = value; break;
            case "UserIdMode": provider.UserIdMode = value; break;
            case "MaxTokensField": provider.MaxTokensField = value; break;
            case "DocumentMode": provider.DocumentMode = value; break;
            case "MidturnSystem": provider.MidturnSystem = value; break;
        }

        ShouldFail(provider, fragment);
    }

    [Fact]
    public void Max_output_tokens_below_sixteen_fails()
    {
        ProviderOptions provider = Bridged();
        provider.MaxOutputTokens = 15;
        ShouldFail(provider, "MaxOutputTokens '15' must be at least 16");
    }

    [Fact]
    public void Catalog_materializes_every_bridge_option()
    {
        ProviderOptions options = Bridged();
        options.CompatProfile = "deepseek";
        options.SendReasoningEffort = true;
        options.UserIdMode = "hash";
        options.MaxTokensField = "max_completion_tokens";
        options.MaxOutputTokens = 8192;
        options.ForwardCacheControl = true;
        options.SendResponseFormat = true;
        options.DocumentMode = "image";
        options.ThinkMarkupExtraction = true;
        options.MidturnSystem = "merge_into_user";

        ProviderCatalog catalog = ProviderCatalogTestFactory.SeededCatalog(new Dictionary<string, ProviderOptions>
        {
            ["chat"] = options
        });

        ProviderRoute route = catalog.ProvidersFor(ApiDialect.Anthropic).Single();
        route.AnthropicChat.IsBridged.ShouldBeTrue();
        route.AnthropicChat.CompatProfile.ShouldBe(AnthropicChatCompatProfile.DeepSeek);
        route.AnthropicChat.SendReasoningEffort.ShouldBeTrue();
        route.AnthropicChat.UserIdMode.ShouldBe(AnthropicChatUserIdMode.Hash);
        route.AnthropicChat.MaxTokensField.ShouldBe(AnthropicChatMaxTokensField.MaxCompletionTokens);
        route.AnthropicChat.MaxOutputTokens.ShouldBe(8192);
        route.AnthropicChat.ForwardCacheControl.ShouldBeTrue();
        route.AnthropicChat.SendResponseFormat.ShouldBeTrue();
        route.AnthropicChat.DocumentMode.ShouldBe(AnthropicChatDocumentMode.Image);
        route.AnthropicChat.ThinkMarkupExtraction.ShouldBeTrue();
        route.AnthropicChat.MidturnSystem.ShouldBe(AnthropicChatMidturnSystem.MergeIntoUser);
    }

    [Fact]
    public void Default_provider_is_not_bridged()
    {
        ProviderCatalog catalog = ProviderCatalogTestFactory.SeededCatalog(new Dictionary<string, ProviderOptions>
        {
            ["messages"] = new() { Dialect = "anthropic", BaseUrl = "https://messages.example" }
        });

        catalog.ProvidersFor(ApiDialect.Anthropic).Single().AnthropicChat.IsBridged.ShouldBeFalse();
    }
}
