using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SmoothLlmImposter.Application.Common.Clients.Optimizer;
using SmoothLlmImposter.Application.Features.Routing;

namespace SmoothLlmImposter.Application.UnitTest.Routing.Optimization;

public sealed class LlmMessagesOptionsSetupTests
{
    [Fact]
    public void Optimizer_transport_resolves_from_the_post_configured_imposter_options()
    {
        using ServiceProvider services = Build(new Dictionary<string, string?>
        {
            ["Imposter:LlmMessages:Enabled"] = "true",
            ["Imposter:LlmMessages:BaseUrl"] = "https://structured.example",
            ["Imposter:LlmMessages:Model"] = "structured-model",
            ["Imposter:LlmMessages:CacheTtlHours"] = "12",
            ["LLM_MESSAGES_BASE_URL"] = "https://conventional.example",
            ["LLM_MESSAGES_MODEL"] = "conventional-model",
            ["LLM_MESSAGES_API_KEY"] = "secret-token",
            ["LLM_MESSAGES_CACHE_TTL_HOURS"] = "24",
            ["LLM_MESSAGES_CIRCUIT_BREAK_SECONDS"] = "30",
            ["LLM_MESSAGES_MAX_ATTEMPTS"] = "2",
            ["LLM_MESSAGES_TIMEOUT"] = "15",
            ["Imposter:LlmMessages:BudgetSeconds"] = "45",
            ["LLM_MESSAGES_BUDGET_SECONDS"] = "90"
        });

        LlmMessagesOptions resolved = services.GetRequiredService<IOptions<LlmMessagesOptions>>().Value;

        resolved.Enabled.ShouldBeTrue();
        resolved.BaseUrl.ShouldBe("https://conventional.example");
        resolved.Model.ShouldBe("conventional-model");
        resolved.ApiKey.ShouldBe("secret-token");
        resolved.CacheTtlHours.ShouldBe(24);
        resolved.CircuitBreakSeconds.ShouldBe(30);
        resolved.MaxAttempts.ShouldBe(2);
        resolved.TimeoutSeconds.ShouldBe(15);
        resolved.BudgetSeconds.ShouldBe(90);
    }

    [Fact]
    public void Structured_budget_binds_without_a_conventional_variable()
    {
        using ServiceProvider services = Build(new Dictionary<string, string?>
        {
            ["Imposter:LlmMessages:BudgetSeconds"] = "45"
        });

        services.GetRequiredService<IOptions<LlmMessagesOptions>>().Value.BudgetSeconds.ShouldBe(45);
    }

    [Fact]
    public void Defaults_match_the_imposter_options_defaults()
    {
        using ServiceProvider services = Build(new Dictionary<string, string?>());

        LlmMessagesOptions resolved = services.GetRequiredService<IOptions<LlmMessagesOptions>>().Value;

        resolved.Enabled.ShouldBeFalse();
        resolved.BaseUrl.ShouldBe(string.Empty);
        resolved.Path.ShouldBe("/v1/chat/completions");
        resolved.Model.ShouldBe(string.Empty);
        resolved.MaxAttempts.ShouldBe(2);
        resolved.RetryDelaySeconds.ShouldBe(5);
        resolved.TimeoutSeconds.ShouldBe(60);
        resolved.CircuitBreakSeconds.ShouldBe(180);
        resolved.CacheTtlHours.ShouldBe(120);
        resolved.MinChars.ShouldBe(0);
        resolved.BudgetSeconds.ShouldBe(30);
        resolved.ApiKey.ShouldBeNull();
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
