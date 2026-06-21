using SmoothLlmImposter.Application.Features.AuthorizationOverride;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.UnitTest.AuthorizationOverride;

public class AuthorizationOverrideSwitchTests
{
    [Fact]
    public void Fresh_instance_reads_off_for_every_dialect()
    {
        var sut = new AuthorizationOverrideSwitch();

        sut.IsEnabled(ApiDialect.OpenAi, "openai-official").ShouldBeFalse();
        sut.IsEnabled(ApiDialect.Anthropic, "anthropic-official").ShouldBeFalse();
    }

    [Fact]
    public void Enable_and_disable_are_isolated_per_dialect()
    {
        var sut = new AuthorizationOverrideSwitch();

        sut.Enable(ApiDialect.Anthropic, "anthropic-official");

        sut.IsEnabled(ApiDialect.Anthropic, "anthropic-official").ShouldBeTrue();
        sut.IsEnabled(ApiDialect.OpenAi, "openai-official").ShouldBeFalse();

        sut.Disable(ApiDialect.Anthropic, "anthropic-official");
        sut.IsEnabled(ApiDialect.Anthropic, "anthropic-official").ShouldBeFalse();
    }

    [Fact]
    public void Enable_and_disable_are_isolated_per_provider()
    {
        var sut = new AuthorizationOverrideSwitch();

        sut.Enable(ApiDialect.OpenAi, "default");

        sut.IsEnabled(ApiDialect.OpenAi, "default").ShouldBeTrue();
        sut.IsEnabled(ApiDialect.OpenAi, "alternate").ShouldBeFalse();
    }

    [Fact]
    public void Provider_keys_compare_case_insensitively()
    {
        // Guards the canonicalisation contract: the switch must agree with the credential stores and
        // ProviderAddressResolver, which all key the provider OrdinalIgnoreCase. Arming under one casing
        // and reading under another must hit the same flag.
        var sut = new AuthorizationOverrideSwitch();

        sut.Enable(ApiDialect.OpenAi, "OpenAI-Default");
        sut.IsEnabled(ApiDialect.OpenAi, "openai-default").ShouldBeTrue();

        sut.Disable(ApiDialect.OpenAi, "OPENAI-DEFAULT");
        sut.IsEnabled(ApiDialect.OpenAi, "openai-default").ShouldBeFalse();
    }

    [Fact]
    public void Enable_is_idempotent()
    {
        var sut = new AuthorizationOverrideSwitch();

        sut.Enable(ApiDialect.OpenAi, "openai-official");
        sut.Enable(ApiDialect.OpenAi, "openai-official");

        sut.IsEnabled(ApiDialect.OpenAi, "openai-official").ShouldBeTrue();
    }
}
