using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmoothLlmImposter.Application.Common.Persistence;
using SmoothLlmImposter.Application.Features.AuthorizationOverride;
using SmoothLlmImposter.Application.Features.Routing;
using SmoothLlmImposter.Domain.Credentials;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.UnitTest.Routing;

public class ImposterRouterTests
{
    private static ImposterRouter Build(
        ICredentialStore? credentialStore = null,
        IAuthorizationOverrideSwitch? overrideSwitch = null)
    {
        var options = Options.Create(new ImposterOptions
        {
            Providers =
            [
                new ProviderOptions
                {
                    Name = "opencode", Dialect = "openai", BaseUrl = "https://opencode.example",
                    Models = [new ModelMappingOptions { From = "gpt5.4", To = "grok-code", Caching = true }]
                },
                new ProviderOptions { Name = "openai-official", Dialect = "openai", BaseUrl = "https://api.openai.com", IsDefault = true }
            ]
        });

        var resolver = new RouteResolver(new ProviderCatalog(options));
        IRequestTransformer[] transformers = [new OpenAiRequestTransformer([]), new AnthropicRequestTransformer()];
        return new ImposterRouter(
            resolver,
            credentialStore ?? new StubCredentialStore(),
            new StubSecretProtector(),
            overrideSwitch ?? new StubOverrideSwitch(),
            transformers,
            NullLogger<ImposterRouter>.Instance);
    }

    [Fact]
    public async Task Plan_resolves_and_transforms_an_imposter_route()
    {
        ImposterRouter router = Build();

        RoutePlan plan = await router.PlanAsync(ApiDialect.OpenAi, """{"model":"gpt5.4"}""", TestContext.Current.CancellationToken);

        plan.InboundModel.ShouldBe("gpt5.4");
        plan.Decision.Provider.Name.ShouldBe("opencode");
        plan.CredentialOverride.ShouldBeNull();
        plan.TransformedBody.ShouldContain("grok-code");
        plan.TransformedBody.ShouldContain("prompt_cache_key");
    }

    [Fact]
    public async Task Plan_applies_active_credential_only_for_passthrough_route()
    {
        var store = new StubCredentialStore
        {
            Active = new OpenAiCredential("work", "cipher:stored-key", CredentialAuthScheme.Bearer, "https://override.example")
        };
        ImposterRouter router = Build(store);

        RoutePlan plan = await router.PlanAsync(ApiDialect.OpenAi, """{"model":"gpt5.5"}""", TestContext.Current.CancellationToken);

        plan.Decision.IsImposter.ShouldBeFalse();
        plan.CredentialOverride.ShouldNotBeNull();
        plan.CredentialOverride.Secret.ShouldBe("stored-key");
        plan.CredentialOverride.BaseUrlOverride!.ToString().ShouldBe("https://override.example/");
    }

    [Fact]
    public async Task Plan_forces_bearer_on_passthrough_when_override_enabled()
    {
        var store = new StubCredentialStore
        {
            Active = new OpenAiCredential("work", "cipher:stored-key", CredentialAuthScheme.ApiKey, baseUrlOverride: null)
        };
        var overrideSwitch = new StubOverrideSwitch { Enabled = true };
        ImposterRouter router = Build(store, overrideSwitch);

        RoutePlan plan = await router.PlanAsync(ApiDialect.OpenAi, """{"model":"gpt5.5"}""", TestContext.Current.CancellationToken);

        plan.Decision.IsImposter.ShouldBeFalse();
        plan.CredentialOverride.ShouldNotBeNull();
        plan.CredentialOverride.ForceBearer.ShouldBeTrue();
        plan.CredentialOverride.Secret.ShouldBe("stored-key");
    }

    [Fact]
    public async Task Plan_fails_closed_with_403_when_override_enabled_and_no_active_credential()
    {
        var overrideSwitch = new StubOverrideSwitch { Enabled = true };
        ImposterRouter router = Build(new StubCredentialStore { Active = null }, overrideSwitch);

        RoutingException ex = await Should.ThrowAsync<RoutingException>(
            () => router.PlanAsync(ApiDialect.OpenAi, """{"model":"gpt5.5"}""", TestContext.Current.CancellationToken));

        ex.StatusCode.ShouldBe(403);
    }

    [Fact]
    public async Task Plan_does_not_force_bearer_on_passthrough_when_override_disabled()
    {
        var store = new StubCredentialStore
        {
            Active = new OpenAiCredential("work", "cipher:stored-key", CredentialAuthScheme.ApiKey, baseUrlOverride: null)
        };
        ImposterRouter router = Build(store, new StubOverrideSwitch { Enabled = false });

        RoutePlan plan = await router.PlanAsync(ApiDialect.OpenAi, """{"model":"gpt5.5"}""", TestContext.Current.CancellationToken);

        plan.CredentialOverride.ShouldNotBeNull();
        plan.CredentialOverride.ForceBearer.ShouldBeFalse();
        plan.CredentialOverride.AuthScheme.ShouldBe(CredentialAuthScheme.ApiKey);
    }

    [Fact]
    public async Task Plan_never_reads_the_override_switch_for_a_matched_imposter_route()
    {
        // A matched imposter route must skip the passthrough seam entirely — the switch (and the store)
        // are never consulted. The throwing spy proves the imposter branch does not read either (LADR-003).
        ImposterRouter router = Build(new ThrowingCredentialStore(), new ThrowingOverrideSwitch());

        RoutePlan plan = await router.PlanAsync(ApiDialect.OpenAi, """{"model":"gpt5.4"}""", TestContext.Current.CancellationToken);

        plan.Decision.IsImposter.ShouldBeTrue();
        plan.CredentialOverride.ShouldBeNull();
    }

    [Fact]
    public async Task Plan_throws_when_model_missing()
    {
        ImposterRouter router = Build();
        await Should.ThrowAsync<RoutingException>(() => router.PlanAsync(ApiDialect.OpenAi, """{"messages":[]}""", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Plan_throws_on_non_object_body()
    {
        ImposterRouter router = Build();
        await Should.ThrowAsync<RoutingException>(() => router.PlanAsync(ApiDialect.OpenAi, "[]", TestContext.Current.CancellationToken));
    }

    private sealed class StubSecretProtector : ISecretProtector
    {
        public string Protect(string plaintext) => "cipher:" + plaintext;

        public string Unprotect(string ciphertext) => ciphertext.Replace("cipher:", string.Empty, StringComparison.Ordinal);
    }

    private sealed class StubCredentialStore : ICredentialStore
    {
        public ProviderCredential? Active { get; init; }

        public Task<ProviderCredential> AddAsync(ProviderCredential credential, CancellationToken cancellationToken) => Task.FromResult(credential);

        public Task<IReadOnlyList<ProviderCredential>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ProviderCredential>>(Array.Empty<ProviderCredential>());

        public Task<ProviderCredential?> GetAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult<ProviderCredential?>(null);

        public Task<ProviderCredential?> GetActiveAsync(ApiDialect dialect, CancellationToken cancellationToken) => Task.FromResult(Active);

        public Task DeleteAsync(Guid id, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<ProviderCredential> UpdateAsync(ProviderCredential credential, CancellationToken cancellationToken) => Task.FromResult(credential);

        public Task<ProviderCredential> ActivateAsync(Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class StubOverrideSwitch : IAuthorizationOverrideSwitch
    {
        public bool Enabled { get; init; }

        public bool IsEnabled(ApiDialect dialect) => Enabled;

        public void Enable(ApiDialect dialect) { }

        public void Disable(ApiDialect dialect) { }
    }

    private sealed class ThrowingOverrideSwitch : IAuthorizationOverrideSwitch
    {
        public bool IsEnabled(ApiDialect dialect) => throw new InvalidOperationException("Imposter route must not read the authorization override switch.");

        public void Enable(ApiDialect dialect) => throw new InvalidOperationException();

        public void Disable(ApiDialect dialect) => throw new InvalidOperationException();
    }

    private sealed class ThrowingCredentialStore : ICredentialStore
    {
        public Task<ProviderCredential> AddAsync(ProviderCredential credential, CancellationToken cancellationToken) => throw new InvalidOperationException();

        public Task<IReadOnlyList<ProviderCredential>> ListAsync(CancellationToken cancellationToken) => throw new InvalidOperationException();

        public Task<ProviderCredential?> GetAsync(Guid id, CancellationToken cancellationToken) => throw new InvalidOperationException();

        public Task<ProviderCredential?> GetActiveAsync(ApiDialect dialect, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Imposter route must not read the credential store.");

        public Task DeleteAsync(Guid id, CancellationToken cancellationToken) => throw new InvalidOperationException();

        public Task<ProviderCredential> UpdateAsync(ProviderCredential credential, CancellationToken cancellationToken) => throw new InvalidOperationException();

        public Task<ProviderCredential> ActivateAsync(Guid id, CancellationToken cancellationToken) => throw new InvalidOperationException();
    }
}
