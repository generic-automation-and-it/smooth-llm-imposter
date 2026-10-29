using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SmoothLlmImposter.Application.Common.Clients.Decisions;
using SmoothLlmImposter.Application.Features.Routing;
using SmoothLlmImposter.Application.Features.Routing.Decisions;

namespace SmoothLlmImposter.Application.UnitTest.Routing.Decisions;

public class DecisionServiceOptionsSetupTests
{
    private const string ToolSection = "Imposter:DecisionsTools";

    [Fact]
    public void Defaults_resolve_to_the_documented_tool_filter_defaults()
    {
        using ServiceProvider services = Build(new Dictionary<string, string?>());

        DecisionServiceOptions resolved = services.GetRequiredService<IOptionsMonitor<DecisionServiceOptions>>().Get(ToolSection);

        resolved.BaseUrl.ShouldBe("http://host.docker.internal:1234");
        resolved.Path.ShouldBe("/v1/systemone");
        resolved.Model.ShouldBe("openjev");
        resolved.MaxAttempts.ShouldBe(2);
        resolved.RetryDelaySeconds.ShouldBe(5);
        resolved.TimeoutSeconds.ShouldBe(60);
    }

    [Fact]
    public void Conventional_variables_win_over_the_structured_section_for_every_transport_setting()
    {
        using ServiceProvider services = Build(new Dictionary<string, string?>
        {
            ["Imposter:DecisionsTools:BaseUrl"] = "https://structured.example",
            ["Imposter:DecisionsTools:Path"] = "/structured",
            ["Imposter:DecisionsTools:Model"] = "structured-model",
            ["Imposter:DecisionsTools:MaxAttempts"] = "2",
            ["Imposter:DecisionsTools:TimeoutSeconds"] = "30",
            ["DECISIONS_TOOLS_BASE_URL"] = "https://conventional.example",
            ["DECISIONS_TOOLS_PATH"] = "/conventional",
            ["DECISIONS_TOOLS_MODEL"] = "conventional-model",
            ["DECISIONS_TOOLS_MAX_ATTEMPTS"] = "5",
            ["DECISIONS_TOOLS_TIMEOUT"] = "90"
        });

        DecisionServiceOptions resolved = services.GetRequiredService<IOptionsMonitor<DecisionServiceOptions>>().Get(ToolSection);

        resolved.BaseUrl.ShouldBe("https://conventional.example");
        resolved.Path.ShouldBe("/conventional");
        resolved.Model.ShouldBe("conventional-model");
        resolved.MaxAttempts.ShouldBe(5);
        resolved.TimeoutSeconds.ShouldBe(90);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_tool_filter_opts_in_through_its_own_enable_flag(bool enabled)
    {
        using ServiceProvider services = Build(new Dictionary<string, string?>
        {
            ["Imposter:DecisionsTools:Enabled"] = enabled.ToString()
        });

        services.GetServices<IDecisionServiceConsumer>().Single(consumer => consumer.Section == ToolSection)
            .IsOptedIn.ShouldBe(enabled);
    }

    [Fact]
    public void A_disabled_section_resolves_to_defaults_so_its_invalid_settings_never_reach_the_transport()
    {
        using ServiceProvider services = Build(new Dictionary<string, string?>
        {
            ["Imposter:DecisionsTools:Enabled"] = "true",
            ["DECISIONS_MESSAGES_BASE_URL"] = "not-a-url",
            ["DECISIONS_MESSAGES_MAX_ATTEMPTS"] = "0"
        });
        var profiles = services.GetRequiredService<DecisionServiceProfiles>();

        DecisionServiceOptions messages = profiles.Resolve("Imposter:DecisionsMessages");

        messages.BaseUrl.ShouldBe(DecisionServiceOptions.DefaultBaseUrl);
        messages.MaxAttempts.ShouldBe(DecisionServiceOptions.DefaultMaxAttempts);
        profiles.Resolve(ToolSection).BaseUrl.ShouldBe(DecisionServiceOptions.DefaultBaseUrl);
    }

    [Fact]
    public void An_unregistered_section_has_no_profile()
    {
        using ServiceProvider services = Build(new Dictionary<string, string?>());

        Should.Throw<InvalidOperationException>(() =>
            services.GetRequiredService<DecisionServiceProfiles>().Resolve("Imposter:DecisionsOther"));
    }

    [Fact]
    public void An_enabled_tool_filter_with_an_invalid_destination_fails_options_validation()
    {
        using ServiceProvider services = Build(new Dictionary<string, string?>
        {
            ["Imposter:DecisionsTools:Enabled"] = "true",
            ["DECISIONS_TOOLS_BASE_URL"] = "not-a-url"
        });

        var failure = Should.Throw<OptionsValidationException>(() =>
            services.GetRequiredService<IOptionsMonitor<DecisionServiceOptions>>().Get(ToolSection));

        failure.Failures.ShouldContain(message => message.Contains("Imposter:DecisionsTools:BaseUrl"));
    }

    private static ServiceProvider Build(Dictionary<string, string?> values)
    {
        values.TryAdd("Imposter:Providers:default:Dialect", "openai");
        values.TryAdd("Imposter:Providers:default:BaseUrl", "https://api.example");
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(configuration);
        services.Configure<ImposterOptions>(configuration.GetSection(ImposterOptions.SectionName));
        services.AddApplication();
        return services.BuildServiceProvider();
    }
}
