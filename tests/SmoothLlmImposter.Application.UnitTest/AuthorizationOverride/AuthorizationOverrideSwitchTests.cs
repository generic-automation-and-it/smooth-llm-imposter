using SmoothLlmImposter.Application.Features.AuthorizationOverride;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.UnitTest.AuthorizationOverride;

public class AuthorizationOverrideSwitchTests
{
    [Fact]
    public void Fresh_instance_reads_off_for_every_dialect()
    {
        var sut = new AuthorizationOverrideSwitch();

        sut.IsEnabled(ApiDialect.OpenAi).ShouldBeFalse();
        sut.IsEnabled(ApiDialect.Anthropic).ShouldBeFalse();
    }

    [Fact]
    public void Enable_and_disable_are_isolated_per_dialect()
    {
        var sut = new AuthorizationOverrideSwitch();

        sut.Enable(ApiDialect.Anthropic);

        sut.IsEnabled(ApiDialect.Anthropic).ShouldBeTrue();
        sut.IsEnabled(ApiDialect.OpenAi).ShouldBeFalse();

        sut.Disable(ApiDialect.Anthropic);
        sut.IsEnabled(ApiDialect.Anthropic).ShouldBeFalse();
    }

    [Fact]
    public void Enable_is_idempotent()
    {
        var sut = new AuthorizationOverrideSwitch();

        sut.Enable(ApiDialect.OpenAi);
        sut.Enable(ApiDialect.OpenAi);

        sut.IsEnabled(ApiDialect.OpenAi).ShouldBeTrue();
    }
}
