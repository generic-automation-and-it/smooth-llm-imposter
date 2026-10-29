using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SmoothLlmImposter.Application.Common.Clients.LocalLlm;
using SmoothLlmImposter.Application.Features.Routing;

namespace SmoothLlmImposter.Application.UnitTest.Routing;

public sealed class LocalLlmGateOptionsTests
{
    [Fact]
    public void The_wait_budget_defaults_to_sixty_seconds()
    {
        using ServiceProvider services = Build(new Dictionary<string, string?>());

        services.GetRequiredService<IOptions<LocalLlmGateOptions>>().Value.WaitSeconds.ShouldBe(60);
    }

    [Fact]
    public void The_conventional_variable_wins_over_the_structured_setting()
    {
        using ServiceProvider services = Build(new Dictionary<string, string?>
        {
            ["Imposter:LocalLlmGateWaitSeconds"] = "30",
            ["LOCAL_LLM_GATE_WAIT_SECONDS"] = "90"
        });

        services.GetRequiredService<IOptions<LocalLlmGateOptions>>().Value.WaitSeconds.ShouldBe(90);
    }

    [Fact]
    public void The_structured_setting_applies_without_a_conventional_variable()
    {
        using ServiceProvider services = Build(new Dictionary<string, string?>
        {
            ["Imposter:LocalLlmGateWaitSeconds"] = "30"
        });

        services.GetRequiredService<IOptions<LocalLlmGateOptions>>().Value.WaitSeconds.ShouldBe(30);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(3600, true)]
    [InlineData(3601, false)]
    public void Startup_validation_bounds_the_wait_budget(int waitSeconds, bool valid)
    {
        var options = new ImposterOptions
        {
            LocalLlmGateWaitSeconds = waitSeconds,
            Providers = { ["default"] = new ProviderOptions { Dialect = "openai", BaseUrl = "https://api.example" } }
        };

        ValidateOptionsResult result = new ImposterOptionsValidator().Validate(null, options);

        result.Failed.ShouldBe(!valid);
        if (!valid)
        {
            result.FailureMessage!.ShouldContain("LocalLlmGateWaitSeconds");
        }
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
