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
    public void Enable_is_idempotent()
    {
        var sut = new AuthorizationOverrideSwitch();

        sut.Enable(ApiDialect.OpenAi, "openai-official");
        sut.Enable(ApiDialect.OpenAi, "openai-official");

        sut.IsEnabled(ApiDialect.OpenAi, "openai-official").ShouldBeTrue();
    }
}
