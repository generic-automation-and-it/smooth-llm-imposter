using Microsoft.Extensions.Logging.Abstractions;
using SmoothLlmImposter.Application.Common.Persistence;
using SmoothLlmImposter.Application.Features.AuthorizationOverride;
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
        var store = new StubCredentialStore { Active = new OpenAiCredential("work", "cipher", CredentialAuthScheme.ApiKey, null) };
        var handler = new SetAuthorizationOverride.Handler(overrideSwitch, store, NullLogger<SetAuthorizationOverride.Handler>.Instance);

        SetAuthorizationOverrideResult result = await handler.Handle(new SetAuthorizationOverride.Request("openai", "tester"), Ct);

        result.Armed.ShouldBeTrue();
        result.State.ShouldBe(new AuthorizationOverrideState("openai", true));
        overrideSwitch.IsEnabled(ApiDialect.OpenAi).ShouldBeTrue();
    }

    [Fact]
    public async Task Set_refuses_and_leaves_switch_off_when_no_active_credential()
    {
        var overrideSwitch = new AuthorizationOverrideSwitch();
        var store = new StubCredentialStore { Active = null };
        var handler = new SetAuthorizationOverride.Handler(overrideSwitch, store, NullLogger<SetAuthorizationOverride.Handler>.Instance);

        SetAuthorizationOverrideResult result = await handler.Handle(new SetAuthorizationOverride.Request("anthropic", "tester"), Ct);

        result.Armed.ShouldBeFalse();
        result.NoActiveCredential.ShouldBeTrue();
        result.State.Enabled.ShouldBeFalse();
        overrideSwitch.IsEnabled(ApiDialect.Anthropic).ShouldBeFalse();
    }

    [Fact]
    public async Task Clear_disables_the_switch()
    {
        var overrideSwitch = new AuthorizationOverrideSwitch();
        overrideSwitch.Enable(ApiDialect.OpenAi);
        var handler = new ClearAuthorizationOverride.Handler(overrideSwitch, NullLogger<ClearAuthorizationOverride.Handler>.Instance);

        AuthorizationOverrideState state = await handler.Handle(new ClearAuthorizationOverride.Request("openai", "tester"), Ct);

        state.ShouldBe(new AuthorizationOverrideState("openai", false));
        overrideSwitch.IsEnabled(ApiDialect.OpenAi).ShouldBeFalse();
    }

    [Fact]
    public async Task Get_reports_current_state()
    {
        var overrideSwitch = new AuthorizationOverrideSwitch();
        overrideSwitch.Enable(ApiDialect.Anthropic);
        var handler = new GetAuthorizationOverride.Handler(overrideSwitch);

        (await handler.Handle(new GetAuthorizationOverride.Request("anthropic"), Ct)).ShouldBe(new AuthorizationOverrideState("anthropic", true));
        (await handler.Handle(new GetAuthorizationOverride.Request("openai"), Ct)).ShouldBe(new AuthorizationOverrideState("openai", false));
    }

    [Theory]
    [InlineData("not-a-dialect")]
    [InlineData("")]
    public void Validators_reject_unknown_dialect(string dialect)
    {
        new SetAuthorizationOverride.Validator().Validate(new SetAuthorizationOverride.Request(dialect, null)).IsValid.ShouldBeFalse();
        new ClearAuthorizationOverride.Validator().Validate(new ClearAuthorizationOverride.Request(dialect, null)).IsValid.ShouldBeFalse();
        new GetAuthorizationOverride.Validator().Validate(new GetAuthorizationOverride.Request(dialect)).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Validators_accept_known_dialects()
    {
        new SetAuthorizationOverride.Validator().Validate(new SetAuthorizationOverride.Request("openai", null)).IsValid.ShouldBeTrue();
        new GetAuthorizationOverride.Validator().Validate(new GetAuthorizationOverride.Request("anthropic")).IsValid.ShouldBeTrue();
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
