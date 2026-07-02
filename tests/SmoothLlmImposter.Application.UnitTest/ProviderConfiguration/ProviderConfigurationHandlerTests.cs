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
                    AuthHeader: "api-key",
                    IsDefault: false,
                    Enabled: true,
                    AnthropicVersion: null,
                    OpenAiUpstreamApi: "chat_completions",
                    RequestNormalization: null,
                    Models: [new ProviderModelMappingBody("gpt5.4", "grok-code", Caching: true)]),
                Actor: "test"),
            TestContext.Current.CancellationToken);

        response.BaseUrl.ShouldBe("https://new.example");
        // AuthHeader is a new field — assert it round-trips through both the response and stored config.
        response.AuthHeader.ShouldBe("api-key");
        registry.TryGet("opencode", out ProviderOptions? stored).ShouldBeTrue();
        stored!.Secret.ShouldBe("sk-existing");
        stored.AuthHeader.ShouldBe("api-key");
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
                    AuthHeader: null,
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
            ["opencode"] = Provider("openai", "https://old.example"),
            ["keep"] = Provider("openai", "https://keep.example")
        });

        var handler = new DeleteProvider.Handler(registry, NullLogger<DeleteProvider.Handler>.Instance);

        (await handler.Handle(new DeleteProvider.Request("opencode", Actor: "test"), TestContext.Current.CancellationToken))
            .ShouldBeTrue();
        registry.TryGet("opencode", out _).ShouldBeFalse();
        registry.TryGet("keep", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Delete_missing_provider_returns_false()
    {
        var registry = new InMemoryProviderRegistry();
        registry.Seed(new Dictionary<string, ProviderOptions>(StringComparer.Ordinal)
        {
            ["opencode"] = Provider("openai", "https://old.example")
        });

        var handler = new DeleteProvider.Handler(registry, NullLogger<DeleteProvider.Handler>.Instance);

        (await handler.Handle(new DeleteProvider.Request("absent", Actor: "test"), TestContext.Current.CancellationToken))
            .ShouldBeFalse();
    }

    [Fact]
    public async Task Delete_rejects_removing_the_last_provider()
    {
        var registry = new InMemoryProviderRegistry();
        registry.Seed(new Dictionary<string, ProviderOptions>(StringComparer.Ordinal)
        {
            ["opencode"] = Provider("openai", "https://old.example")
        });

        var handler = new DeleteProvider.Handler(registry, NullLogger<DeleteProvider.Handler>.Instance);

        // Deleting the only provider would leave an empty registry, which the startup validator rejects;
        // the delete handler enforces the same invariant at runtime (HLD 008 — config reseeds on restart).
        await Should.ThrowAsync<ValidationException>(() => handler.Handle(
            new DeleteProvider.Request("opencode", Actor: "test"),
            TestContext.Current.CancellationToken).AsTask());
        registry.TryGet("opencode", out _).ShouldBeTrue();
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
