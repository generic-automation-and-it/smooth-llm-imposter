using Microsoft.Extensions.Options;
using SmoothLlmImposter.Application.Features.GithubCopilot;
using Shouldly;
using Xunit;

namespace SmoothLlmImposter.Application.UnitTest.Features.GithubCopilot;

public sealed class GithubCopilotOptionsPostConfigureTests
{
    private const string EnabledVar = GithubCopilotOptionsPostConfigure.EnabledEnvironmentVariable;
    private const string ClientIdVar = GithubCopilotOptionsPostConfigure.ClientIdEnvironmentVariable;

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData(" ", false)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("1", true)]
    public void Enabled_env_var_maps_onto_the_feature_flag(string? rawValue, bool expected)
    {
        Environment.SetEnvironmentVariable(EnabledVar, rawValue);
        try
        {
            var options = new GithubCopilotOptions();
            new GithubCopilotOptionsPostConfigure().PostConfigure(null, options);

            options.Enabled.ShouldBe(expected);
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnabledVar, null);
        }
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("false")]
    [InlineData("on")]
    public void Malformed_enabled_value_fails_startup(string rawValue)
    {
        Environment.SetEnvironmentVariable(EnabledVar, rawValue);
        try
        {
            var failure = Should.Throw<OptionsValidationException>(
                () => new GithubCopilotOptionsPostConfigure().PostConfigure(null, new GithubCopilotOptions()));

            failure.Message.ShouldContain(EnabledVar);
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnabledVar, null);
        }
    }

    [Fact]
    public void Client_id_env_binds_when_present()
    {
        Environment.SetEnvironmentVariable(ClientIdVar, "test-client-id");
        try
        {
            var options = new GithubCopilotOptions();
            new GithubCopilotOptionsPostConfigure().PostConfigure(null, options);

            options.ClientId.ShouldBe("test-client-id");
        }
        finally
        {
            Environment.SetEnvironmentVariable(ClientIdVar, null);
        }
    }

    [Fact]
    public void Blank_client_id_env_is_treated_as_absent()
    {
        Environment.SetEnvironmentVariable(ClientIdVar, "   ");
        try
        {
            var options = new GithubCopilotOptions { ClientId = "from-config" };
            new GithubCopilotOptionsPostConfigure().PostConfigure(null, options);

            options.ClientId.ShouldBe("from-config");
        }
        finally
        {
            Environment.SetEnvironmentVariable(ClientIdVar, null);
        }
    }
}
