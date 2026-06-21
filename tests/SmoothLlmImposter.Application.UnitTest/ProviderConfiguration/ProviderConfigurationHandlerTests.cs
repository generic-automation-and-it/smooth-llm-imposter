using FluentValidation;
using Microsoft.Extensions.Logging.Abstractions;
using SmoothLlmImposter.Application.Features.ProviderConfiguration;
using SmoothLlmImposter.Application.Features.Routing;

namespace SmoothLlmImposter.Application.UnitTest.ProviderConfiguration;

public class ProviderConfigurationHandlerTests
{
    [Fact]
    public async Task Upsert_preserves_existing_secret_and_replaces_runtime_config()
    {
        var registry = new InMemoryProviderRegistry();
        registry.Seed(new Dictionary<string, ProviderOptions>(StringComparer.Ordinal)
        {
            ["opencode"] = Provider("openai", "https://old.example", secret: "sk-existing")
        });

        var handler = new UpsertProvider.Handler(registry, NullLogger<UpsertProvider.Handler>.Instance);

        ProviderConfigurationResponse response = await handler.Handle(
            new UpsertProvider.Request(
                "opencode",
                new ProviderConfigurationBody(
                    Name: null,
                    Dialect: "openai",
                    BaseUrl: "https://new.example",
                    AuthScheme: "ApiKey",
                    IsDefault: false,
                    Enabled: true,
                    AnthropicVersion: null,
                    OpenAiUpstreamApi: "chat_completions",
                    RequestNormalization: null,
                    Models: [new ProviderModelMappingBody("gpt5.4", "grok-code", Caching: true)]),
                Actor: "test"),
            TestContext.Current.CancellationToken);

        response.BaseUrl.ShouldBe("https://new.example");
        registry.TryGet("opencode", out ProviderOptions stored).ShouldBeTrue();
        stored.Secret.ShouldBe("sk-existing");
        stored.Models.Single().To.ShouldBe("grok-code");
    }

    [Fact]
    public async Task Upsert_rejects_second_enabled_default_for_same_dialect()
    {
        var registry = new InMemoryProviderRegistry();
        registry.Seed(new Dictionary<string, ProviderOptions>(StringComparer.Ordinal)
        {
            ["default"] = Provider("openai", "https://default.example", isDefault: true)
        });

        var handler = new UpsertProvider.Handler(registry, NullLogger<UpsertProvider.Handler>.Instance);

        await Should.ThrowAsync<ValidationException>(() => handler.Handle(
            new UpsertProvider.Request(
                "second",
                new ProviderConfigurationBody(
                    Name: null,
                    Dialect: "openai",
                    BaseUrl: "https://second.example",
                    AuthScheme: null,
                    IsDefault: true,
                    Enabled: true,
                    AnthropicVersion: null,
                    OpenAiUpstreamApi: null,
                    RequestNormalization: null,
                    Models: []),
                Actor: "test"),
            TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task Disabled_default_can_coexist_with_enabled_default()
    {
        var registry = new InMemoryProviderRegistry();
        registry.Seed(new Dictionary<string, ProviderOptions>(StringComparer.Ordinal)
        {
            ["disabled-default"] = WithEnabled(Provider("openai", "https://disabled.example", isDefault: true), false),
            ["enabled-default"] = Provider("openai", "https://enabled.example", isDefault: true)
        });

        var handler = new SetProviderEnabled.Handler(registry, NullLogger<SetProviderEnabled.Handler>.Instance);
        ProviderConfigurationResponse? response = await handler.Handle(
            new SetProviderEnabled.Request("disabled-default", Enabled: false, Actor: "test"),
            TestContext.Current.CancellationToken);

        response.ShouldNotBeNull();
        response.Enabled.ShouldBeFalse();
    }

    [Fact]
    public async Task Delete_removes_provider()
    {
        var registry = new InMemoryProviderRegistry();
        registry.Seed(new Dictionary<string, ProviderOptions>(StringComparer.Ordinal)
        {
            ["opencode"] = Provider("openai", "https://old.example")
        });

        var handler = new DeleteProvider.Handler(registry, NullLogger<DeleteProvider.Handler>.Instance);

        (await handler.Handle(new DeleteProvider.Request("opencode", Actor: "test"), TestContext.Current.CancellationToken))
            .ShouldBeTrue();
        registry.TryGet("opencode", out _).ShouldBeFalse();
    }

    private static ProviderOptions Provider(string dialect, string baseUrl, bool isDefault = false, string? secret = null) => new()
    {
        Dialect = dialect,
        BaseUrl = baseUrl,
        IsDefault = isDefault,
        Secret = secret
    };

    private static ProviderOptions WithEnabled(ProviderOptions provider, bool enabled)
    {
        provider.Enabled = enabled;
        return provider;
    }
}
