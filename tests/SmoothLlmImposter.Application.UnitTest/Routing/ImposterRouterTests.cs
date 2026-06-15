using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmoothLlmImposter.Application.Common.Persistence;
using SmoothLlmImposter.Application.Features.Routing;
using SmoothLlmImposter.Domain.Credentials;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.UnitTest.Routing;

public class ImposterRouterTests
{
    private static ImposterRouter Build(ICredentialStore? credentialStore = null)
    {
        var options = Options.Create(new ImposterOptions
        {
            Providers =
            [
                new ProviderOptions
                {
                    Name = "opencode", Api = "openai", BaseUrl = "https://opencode.example",
                    Models = [new ModelMappingOptions { From = "gpt5.4", To = "grok-code", Caching = true }]
                },
                new ProviderOptions { Name = "openai-official", Api = "openai", BaseUrl = "https://api.openai.com", IsDefault = true }
            ]
        });

        var resolver = new RouteResolver(new ProviderCatalog(options));
        IRequestTransformer[] transformers = [new OpenAiRequestTransformer(), new AnthropicRequestTransformer()];
        return new ImposterRouter(
            resolver,
            credentialStore ?? new StubCredentialStore(),
            new StubSecretProtector(),
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
    public void Plan_throws_when_model_missing()
    {
        ImposterRouter router = Build();
        Should.ThrowAsync<RoutingException>(() => router.PlanAsync(ApiDialect.OpenAi, """{"messages":[]}""", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Plan_throws_on_non_object_body()
    {
        ImposterRouter router = Build();
        Should.ThrowAsync<RoutingException>(() => router.PlanAsync(ApiDialect.OpenAi, "[]", TestContext.Current.CancellationToken));
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
}
