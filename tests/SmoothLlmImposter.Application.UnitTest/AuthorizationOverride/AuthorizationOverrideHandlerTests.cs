using Microsoft.Extensions.Logging.Abstractions;
using SmoothLlmImposter.Application.Common.Persistence;
using SmoothLlmImposter.Application.Features.AuthorizationOverride;
using SmoothLlmImposter.Application.Features.Routing;
using SmoothLlmImposter.Application.UnitTest.Routing;
using SmoothLlmImposter.Domain.Credentials;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.UnitTest.AuthorizationOverride;

public class AuthorizationOverrideHandlerTests
{
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Set_arms_switch_when_an_active_credential_exists()
    {
        var overrideSwitch = new AuthorizationOverrideSwitch();
        var store = new StubCredentialStore { Active = new OpenAiCredential("openai-official", "work", "cipher", CredentialAuthScheme.ApiKey, null) };
        var handler = new SetAuthorizationOverride.Handler(overrideSwitch, store, Catalog(), NullLogger<SetAuthorizationOverride.Handler>.Instance);

        SetAuthorizationOverrideResult result = await handler.Handle(new SetAuthorizationOverride.Request("openai", null, "tester"), Ct);

        result.Armed.ShouldBeTrue();
        result.State.ShouldBe(new AuthorizationOverrideState("openai", "openai-official", true));
        overrideSwitch.IsEnabled(ApiDialect.OpenAi, "openai-official").ShouldBeTrue();
    }

    [Fact]
    public async Task Set_refuses_and_leaves_switch_off_when_no_active_credential()
    {
        var overrideSwitch = new AuthorizationOverrideSwitch();
        var store = new StubCredentialStore { Active = null };
        var handler = new SetAuthorizationOverride.Handler(overrideSwitch, store, Catalog(), NullLogger<SetAuthorizationOverride.Handler>.Instance);

        SetAuthorizationOverrideResult result = await handler.Handle(new SetAuthorizationOverride.Request("anthropic", null, "tester"), Ct);

        result.Armed.ShouldBeFalse();
        result.NoActiveCredential.ShouldBeTrue();
        result.State.Enabled.ShouldBeFalse();
        result.State.ProviderName.ShouldBe("anthropic-official");
        overrideSwitch.IsEnabled(ApiDialect.Anthropic, "anthropic-official").ShouldBeFalse();
    }

    [Fact]
    public async Task Clear_disables_the_switch()
    {
        var overrideSwitch = new AuthorizationOverrideSwitch();
        overrideSwitch.Enable(ApiDialect.OpenAi, "openai-official");
        var handler = new ClearAuthorizationOverride.Handler(overrideSwitch, Catalog(), NullLogger<ClearAuthorizationOverride.Handler>.Instance);

        AuthorizationOverrideState state = await handler.Handle(new ClearAuthorizationOverride.Request("openai", null, "tester"), Ct);

        state.ShouldBe(new AuthorizationOverrideState("openai", "openai-official", false));
        overrideSwitch.IsEnabled(ApiDialect.OpenAi, "openai-official").ShouldBeFalse();
    }

    [Fact]
    public async Task Get_reports_current_state()
    {
        var overrideSwitch = new AuthorizationOverrideSwitch();
        overrideSwitch.Enable(ApiDialect.Anthropic, "anthropic-official");
        var handler = new GetAuthorizationOverride.Handler(overrideSwitch, Catalog());

        (await handler.Handle(new GetAuthorizationOverride.Request("anthropic", null), Ct)).ShouldBe(new AuthorizationOverrideState("anthropic", "anthropic-official", true));
        (await handler.Handle(new GetAuthorizationOverride.Request("openai", null), Ct)).ShouldBe(new AuthorizationOverrideState("openai", "openai-official", false));
    }

    [Theory]
    [InlineData("not-a-dialect")]
    [InlineData("")]
    public void Validators_reject_unknown_dialect(string dialect)
    {
        new SetAuthorizationOverride.Validator().Validate(new SetAuthorizationOverride.Request(dialect, null, null)).IsValid.ShouldBeFalse();
        new ClearAuthorizationOverride.Validator().Validate(new ClearAuthorizationOverride.Request(dialect, null, null)).IsValid.ShouldBeFalse();
        new GetAuthorizationOverride.Validator().Validate(new GetAuthorizationOverride.Request(dialect, null)).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Validators_accept_known_dialects()
    {
        new SetAuthorizationOverride.Validator().Validate(new SetAuthorizationOverride.Request("openai", null, null)).IsValid.ShouldBeTrue();
        new GetAuthorizationOverride.Validator().Validate(new GetAuthorizationOverride.Request("anthropic", null)).IsValid.ShouldBeTrue();
    }

    private static IProviderCatalog Catalog() =>
        new ProviderCatalog(new StaticOptionsSnapshot<ImposterOptions>(new ImposterOptions
        {
            Providers =
            {
                ["openai-official"] = new ProviderOptions { Dialect = "openai", BaseUrl = "https://api.openai.test", IsDefault = true },
                ["anthropic-official"] = new ProviderOptions { Dialect = "anthropic", BaseUrl = "https://api.anthropic.test", IsDefault = true }
            }
        }));

    private sealed class StubCredentialStore : ICredentialStore
    {
        public ProviderCredential? Active { get; init; }

        public Task<ProviderCredential> AddAsync(ProviderCredential credential, CancellationToken cancellationToken) => Task.FromResult(credential);

        public Task<IReadOnlyList<ProviderCredential>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ProviderCredential>>(Array.Empty<ProviderCredential>());

        public Task<ProviderCredential?> GetAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult<ProviderCredential?>(null);

        public Task<ProviderCredential?> GetActiveAsync(ApiDialect dialect, string providerName, CancellationToken cancellationToken) => Task.FromResult(Active);

        public Task DeleteAsync(Guid id, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<ProviderCredential> UpdateAsync(ProviderCredential credential, CancellationToken cancellationToken) => Task.FromResult(credential);

        public Task<ProviderCredential> ActivateAsync(Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
