using FluentValidation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmoothLlmImposter.Application.Features.GithubCopilot;
using SmoothLlmImposter.Application.Features.ProviderConfiguration;
using SmoothLlmImposter.Application.Features.Routing;
using SmoothLlmImposter.Application.UnitTest.Features.GithubCopilot;

namespace SmoothLlmImposter.Application.UnitTest.ProviderConfiguration;

/// <summary>
/// L0 — the D1 reserved-key matrix enforced by the generic provider slices: feature-owned creation and
/// deletion, fixed identity on an existing key, and Models/Enabled remaining editable.
/// </summary>
public class GithubCopilotProviderOwnershipTests
{
    private const string AnthropicKey = "company1-github-anthropic";

    private static GithubCopilotProviderOwnership Ownership(StubConnectionStore store, bool enabled = true, GithubCopilotPrefixGate? gate = null) =>
        new(Options.Create(new GithubCopilotOptions { Enabled = enabled, ClientId = null }), store, gate ?? new GithubCopilotPrefixGate());

    private static ProviderOptions CopilotProvider(string dialect, string path) => new()
    {
        Dialect = dialect,
        BaseUrl = "https://api.githubcopilot.com",
        AuthScheme = "Bearer",
        IsDefault = false,
        OpenAiUpstreamApi = "responses",
        RequestNormalization = "none",
        Models = [new ModelMappingOptions { From = path, To = "target" }]
    };

    private static ProviderConfigurationBody Body(
        string dialect = "anthropic",
        string baseUrl = "https://api.githubcopilot.com",
        string? authScheme = "Bearer",
        string? authHeader = null,
        bool isDefault = false,
        bool enabled = true,
        string? openAiUpstreamApi = "responses",
        string? requestNormalization = "none",
        bool? stripEncryptedContent = null,
        IReadOnlyList<ProviderModelMappingBody>? models = null) =>
        new(
            Name: null,
            Dialect: dialect,
            BaseUrl: baseUrl,
            AuthScheme: authScheme,
            AuthHeader: authHeader,
            IsDefault: isDefault,
            Enabled: enabled,
            AnthropicVersion: null,
            OpenAiUpstreamApi: openAiUpstreamApi,
            RequestNormalization: requestNormalization,
            SessionForwarding: null,
            StripEncryptedContent: stripEncryptedContent,
            TimeoutSeconds: null,
            Models: models ?? []);

    [Fact]
    public async Task Generic_upsert_cannot_create_a_missing_reserved_key()
    {
        var harness = new GithubCopilotHarness();
        var handler = new UpsertProvider.Handler(harness.Registry, Ownership(harness.Store), NullLogger<UpsertProvider.Handler>.Instance);

        await Should.ThrowAsync<GithubCopilotProviderConflictException>(() => handler.Handle(
            new UpsertProvider.Request(AnthropicKey, Body(), Actor: "test"),
            TestContext.Current.CancellationToken).AsTask());

        harness.Registry.TryGet(AnthropicKey, out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("openai", "Bearer", null, false, "https://api.githubcopilot.com", "responses", "none")]
    [InlineData("anthropic", "ApiKey", null, false, "https://api.githubcopilot.com", "responses", "none")]
    [InlineData("anthropic", null, null, false, "https://api.githubcopilot.com", "responses", "none")]
    [InlineData("anthropic", "Bearer", "api-key", false, "https://api.githubcopilot.com", "responses", "none")]
    [InlineData("anthropic", "Bearer", null, true, "https://api.githubcopilot.com", "responses", "none")]
    [InlineData("anthropic", "Bearer", null, false, "https://other.example", "responses", "none")]
    [InlineData("anthropic", "Bearer", null, false, "https://api.githubcopilot.com", "chat_completions", "none")]
    [InlineData("anthropic", "Bearer", null, false, "https://api.githubcopilot.com", "responses", "codex_to_openai_sdk")]
    public async Task Generic_upsert_rejects_a_fixed_identity_change(
        string dialect,
        string? authScheme,
        string? authHeader,
        bool isDefault,
        string baseUrl,
        string? openAiUpstreamApi,
        string? normalization)
    {
        var harness = new GithubCopilotHarness();
        harness.Registry.Seed(new Dictionary<string, ProviderOptions>(StringComparer.Ordinal)
        {
            ["ordinary"] = new() { Dialect = "openai", BaseUrl = "https://ordinary.example" },
            [AnthropicKey] = CopilotProvider("anthropic", "/v1/messages")
        });
        var handler = new UpsertProvider.Handler(harness.Registry, Ownership(harness.Store), NullLogger<UpsertProvider.Handler>.Instance);

        ValidationException ex = await Should.ThrowAsync<ValidationException>(() => handler.Handle(
            new UpsertProvider.Request(
                AnthropicKey,
                Body(dialect, baseUrl, authScheme, authHeader, isDefault, true, openAiUpstreamApi, normalization),
                Actor: "test"),
            TestContext.Current.CancellationToken).AsTask());

        ex.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Generic_upsert_rejects_enabling_encrypted_content_stripping()
    {
        var harness = new GithubCopilotHarness();
        harness.Registry.Seed(new Dictionary<string, ProviderOptions>(StringComparer.Ordinal)
        {
            ["ordinary"] = new() { Dialect = "openai", BaseUrl = "https://ordinary.example" },
            [AnthropicKey] = CopilotProvider("anthropic", "/v1/messages")
        });
        var handler = new UpsertProvider.Handler(harness.Registry, Ownership(harness.Store), NullLogger<UpsertProvider.Handler>.Instance);

        ValidationException ex = await Should.ThrowAsync<ValidationException>(() => handler.Handle(
            new UpsertProvider.Request(AnthropicKey, Body(stripEncryptedContent: true), Actor: "test"),
            TestContext.Current.CancellationToken).AsTask());

        ex.Errors.ShouldContain(failure => failure.PropertyName == "StripEncryptedContent");
    }

    [Fact]
    public async Task Generic_upsert_treats_a_trailing_slash_on_the_origin_as_unchanged_identity()
    {
        var harness = new GithubCopilotHarness();
        harness.Registry.Seed(new Dictionary<string, ProviderOptions>(StringComparer.Ordinal)
        {
            ["ordinary"] = new() { Dialect = "openai", BaseUrl = "https://ordinary.example" },
            [AnthropicKey] = CopilotProvider("anthropic", "/v1/messages")
        });
        var handler = new UpsertProvider.Handler(harness.Registry, Ownership(harness.Store), NullLogger<UpsertProvider.Handler>.Instance);

        ProviderConfigurationResponse response = await handler.Handle(
            new UpsertProvider.Request(AnthropicKey, Body(baseUrl: "https://api.githubcopilot.com/"), Actor: "test"),
            TestContext.Current.CancellationToken);

        response.Key.ShouldBe(AnthropicKey);
    }

    [Fact]
    public async Task Generic_upsert_accepts_a_mapping_and_enabled_edit_with_unchanged_identity()
    {
        var harness = new GithubCopilotHarness();
        harness.Registry.Seed(new Dictionary<string, ProviderOptions>(StringComparer.Ordinal)
        {
            ["ordinary"] = new() { Dialect = "openai", BaseUrl = "https://ordinary.example" },
            [AnthropicKey] = CopilotProvider("anthropic", "/v1/messages")
        });
        var handler = new UpsertProvider.Handler(harness.Registry, Ownership(harness.Store), NullLogger<UpsertProvider.Handler>.Instance);

        ProviderConfigurationResponse response = await handler.Handle(
            new UpsertProvider.Request(
                AnthropicKey,
                Body(models: [new ProviderModelMappingBody("claude-*", "claude-haiku-4.5", Caching: false)]),
                Actor: "test"),
            TestContext.Current.CancellationToken);

        response.Models.Single().To.ShouldBe("claude-haiku-4.5");
        harness.Registry.TryGet(AnthropicKey, out ProviderOptions? stored).ShouldBeTrue();
        stored!.AuthScheme.ShouldBe("Bearer");
        stored.Secret.ShouldBeNull();
        stored.Models.Single().From.ShouldBe("claude-*");
    }

    [Fact]
    public async Task Reserved_upsert_and_enable_wait_for_the_per_prefix_gate()
    {
        var harness = new GithubCopilotHarness();
        harness.Registry.Seed(new Dictionary<string, ProviderOptions>(StringComparer.Ordinal)
        {
            ["ordinary"] = new() { Dialect = "openai", BaseUrl = "https://ordinary.example" },
            [AnthropicKey] = CopilotProvider("anthropic", "/v1/messages")
        });
        GithubCopilotProviderOwnership ownership = Ownership(harness.Store, gate: harness.Gate);
        var upsert = new UpsertProvider.Handler(harness.Registry, ownership, NullLogger<UpsertProvider.Handler>.Instance);
        var enable = new SetProviderEnabled.Handler(harness.Registry, ownership, NullLogger<SetProviderEnabled.Handler>.Instance);

        // Holding the gate must make both reserved mutations yield before touching the registry; without the
        // gate they would complete synchronously and race a concurrent login publication (D2).
        ValueTask<ProviderConfigurationResponse> pendingUpsert;
        ValueTask<ProviderConfigurationResponse?> pendingEnable;
        using (await ownership.AcquireAsync("company1", TestContext.Current.CancellationToken))
        {
            pendingUpsert = upsert.Handle(
                new UpsertProvider.Request(AnthropicKey, Body(models: [new ProviderModelMappingBody("a", "b", false)]), Actor: "test"),
                TestContext.Current.CancellationToken);
            pendingEnable = enable.Handle(
                new SetProviderEnabled.Request(AnthropicKey, Enabled: false, Actor: "test"),
                TestContext.Current.CancellationToken);

            pendingUpsert.IsCompleted.ShouldBeFalse();
            pendingEnable.IsCompleted.ShouldBeFalse();
        }

        await pendingUpsert;
        await pendingEnable;

        harness.Registry.TryGet(AnthropicKey, out ProviderOptions? stored).ShouldBeTrue();
        stored!.Enabled.ShouldBeFalse();
    }

    [Fact]
    public async Task Delete_is_refused_while_a_durable_connection_row_exists()
    {
        var harness = new GithubCopilotHarness();
        harness.Registry.Seed(new Dictionary<string, ProviderOptions>(StringComparer.Ordinal)
        {
            ["ordinary"] = new() { Dialect = "openai", BaseUrl = "https://ordinary.example" },
            [AnthropicKey] = CopilotProvider("anthropic", "/v1/messages")
        });
        harness.Store.Upsert(new GithubCopilotConnection(
            "company1", "conn", "octocat", "client", "https://api.githubcopilot.com",
            "enc:access", null, "2020-01-01T00:00:00Z", 1)); // expired row still reserves the keys

        var handler = new DeleteProvider.Handler(harness.Registry, Ownership(harness.Store), NullLogger<DeleteProvider.Handler>.Instance);

        await Should.ThrowAsync<GithubCopilotProviderConflictException>(() => handler.Handle(
            new DeleteProvider.Request(AnthropicKey, Actor: "test"),
            TestContext.Current.CancellationToken).AsTask());

        harness.Registry.TryGet(AnthropicKey, out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Delete_cleans_up_an_orphan_key_when_no_connection_exists()
    {
        var harness = new GithubCopilotHarness();
        harness.Registry.Seed(new Dictionary<string, ProviderOptions>(StringComparer.Ordinal)
        {
            ["ordinary"] = new() { Dialect = "openai", BaseUrl = "https://ordinary.example" },
            [AnthropicKey] = CopilotProvider("anthropic", "/v1/messages"),
            ["company1-github-openai"] = CopilotProvider("openai", "/responses")
        });

        var handler = new DeleteProvider.Handler(harness.Registry, Ownership(harness.Store), NullLogger<DeleteProvider.Handler>.Instance);

        (await handler.Handle(new DeleteProvider.Request(AnthropicKey, Actor: "test"), TestContext.Current.CancellationToken))
            .ShouldBeTrue();
        harness.Registry.TryGet(AnthropicKey, out _).ShouldBeFalse();
        harness.Registry.TryGet("company1-github-openai", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Reserved_mutations_fail_as_feature_disabled_without_touching_the_store()
    {
        var harness = new GithubCopilotHarness(enabled: false);
        harness.Registry.Seed(new Dictionary<string, ProviderOptions>(StringComparer.Ordinal)
        {
            ["ordinary"] = new() { Dialect = "openai", BaseUrl = "https://ordinary.example" },
            [AnthropicKey] = CopilotProvider("anthropic", "/v1/messages")
        });
        GithubCopilotProviderOwnership ownership = Ownership(harness.Store, enabled: false);

        var upsert = new UpsertProvider.Handler(harness.Registry, ownership, NullLogger<UpsertProvider.Handler>.Instance);
        var delete = new DeleteProvider.Handler(harness.Registry, ownership, NullLogger<DeleteProvider.Handler>.Instance);
        var enable = new SetProviderEnabled.Handler(harness.Registry, ownership, NullLogger<SetProviderEnabled.Handler>.Instance);

        await Should.ThrowAsync<GithubCopilotFeatureDisabledException>(() => upsert.Handle(
            new UpsertProvider.Request(AnthropicKey, Body(), Actor: "test"), TestContext.Current.CancellationToken).AsTask());
        await Should.ThrowAsync<GithubCopilotFeatureDisabledException>(() => delete.Handle(
            new DeleteProvider.Request(AnthropicKey, Actor: "test"), TestContext.Current.CancellationToken).AsTask());
        await Should.ThrowAsync<GithubCopilotFeatureDisabledException>(() => enable.Handle(
            new SetProviderEnabled.Request(AnthropicKey, Enabled: false, Actor: "test"), TestContext.Current.CancellationToken).AsTask());
    }
}
