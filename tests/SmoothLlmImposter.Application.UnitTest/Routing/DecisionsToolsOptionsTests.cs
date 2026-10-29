using System.Globalization;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmoothLlmImposter.Application.Common.Clients.Decisions;
using SmoothLlmImposter.Application.Features.Routing;
using SmoothLlmImposter.Application.Features.Routing.Decisions;

namespace SmoothLlmImposter.Application.UnitTest.Routing;

public class DecisionsToolsOptionsTests
{
    [Fact]
    public void Defaults_keep_filter_disabled_and_target_the_container_host()
    {
        DecisionsToolsOptions options = new ImposterOptions().DecisionsTools;

        options.Enabled.ShouldBeFalse();
        options.BaseUrl.ShouldBe("http://host.docker.internal:1234");
        options.Path.ShouldBe("/v1/systemone");
        options.Model.ShouldBe("openjev");
        options.MinProbability.ShouldBe(0.50);
        options.MinKeepTools.ShouldBe(20);
        options.AlwaysKeep.ShouldBe("Bash,Read,Edit,Write,Skill");
        options.StickyPerSession.ShouldBeFalse();
        options.MaxAttempts.ShouldBe(3);
        options.TimeoutSeconds.ShouldBe(60);
        options.CircuitBreakSeconds.ShouldBe(180);
    }

    [Fact]
    public void Structured_section_binds_every_decision_setting()
    {
        using ServiceProvider services = new ServiceCollection()
            .Configure<ImposterOptions>(Configuration(new Dictionary<string, string?>
            {
                ["Imposter:DecisionsTools:Enabled"] = "true",
                ["Imposter:DecisionsTools:BaseUrl"] = "http://127.0.0.1:4321",
                ["Imposter:DecisionsTools:Path"] = "/classify",
                ["Imposter:DecisionsTools:Model"] = "other-model",
                ["Imposter:DecisionsTools:MinProbability"] = "0.75",
                ["Imposter:DecisionsTools:MinKeepTools"] = "12",
                ["Imposter:DecisionsTools:AlwaysKeep"] = "shell,apply_patch",
                ["Imposter:DecisionsTools:StickyPerSession"] = "true",
                ["Imposter:DecisionsTools:MaxAttempts"] = "5",
                ["Imposter:DecisionsTools:TimeoutSeconds"] = "90",
                ["Imposter:DecisionsTools:CircuitBreakSeconds"] = "45"
            }).GetSection(ImposterOptions.SectionName))
            .BuildServiceProvider();

        DecisionsToolsOptions options = services.GetRequiredService<IOptions<ImposterOptions>>().Value.DecisionsTools;

        options.Enabled.ShouldBeTrue();
        options.BaseUrl.ShouldBe("http://127.0.0.1:4321");
        options.Path.ShouldBe("/classify");
        options.Model.ShouldBe("other-model");
        options.MinProbability.ShouldBe(0.75);
        options.MinKeepTools.ShouldBe(12);
        options.AlwaysKeep.ShouldBe("shell,apply_patch");
        options.StickyPerSession.ShouldBeTrue();
        options.MaxAttempts.ShouldBe(5);
        options.TimeoutSeconds.ShouldBe(90);
        options.CircuitBreakSeconds.ShouldBe(45);
    }

    [Fact]
    public void All_conventional_variables_override_the_bound_section()
    {
        ImposterOptions options = BoundOptions();
        Apply(options, new Dictionary<string, string?>
        {
            ["DECISIONS_TOOLS_ENABLED"] = "false",
            ["DECISIONS_TOOLS_BASE_URL"] = "http://127.0.0.1:4321",
            ["DECISIONS_TOOLS_PATH"] = "/classify",
            ["DECISIONS_TOOLS_MODEL"] = "other-model",
            ["DECISIONS_TOOLS_MIN_PROBABILITY"] = "0.75",
            ["DECISIONS_TOOLS_MIN_KEEP"] = "12",
            ["DECISIONS_TOOLS_ALWAYS_KEEP"] = "shell,apply_patch",
            ["DECISIONS_TOOLS_STICKY_PER_SESSION"] = "true",
            ["DECISIONS_TOOLS_MAX_ATTEMPTS"] = "5",
            ["DECISIONS_TOOLS_TIMEOUT"] = "90",
            ["DECISIONS_TOOLS_CIRCUIT_BREAK_SECONDS"] = "45"
        });

        options.DecisionsTools.Enabled.ShouldBeFalse();
        options.DecisionsTools.BaseUrl.ShouldBe("http://127.0.0.1:4321");
        options.DecisionsTools.Path.ShouldBe("/classify");
        options.DecisionsTools.Model.ShouldBe("other-model");
        options.DecisionsTools.MinProbability.ShouldBe(0.75);
        options.DecisionsTools.MinKeepTools.ShouldBe(12);
        options.DecisionsTools.AlwaysKeep.ShouldBe("shell,apply_patch");
        options.DecisionsTools.StickyPerSession.ShouldBeTrue();
        options.DecisionsTools.MaxAttempts.ShouldBe(5);
        options.DecisionsTools.TimeoutSeconds.ShouldBe(90);
        options.DecisionsTools.CircuitBreakSeconds.ShouldBe(45);
    }

    [Fact]
    public void Conventional_enabled_turns_the_default_filter_on()
    {
        var options = new ImposterOptions();

        Apply(options, new Dictionary<string, string?> { ["DECISIONS_TOOLS_ENABLED"] = "true" });

        options.DecisionsTools.Enabled.ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Absent_or_blank_variables_preserve_bound_settings(string? value)
    {
        ImposterOptions options = BoundOptions();
        CapturingLogger logger = Apply(options, new Dictionary<string, string?>
        {
            ["DECISIONS_TOOLS_ENABLED"] = value,
            ["DECISIONS_TOOLS_BASE_URL"] = value,
            ["DECISIONS_TOOLS_PATH"] = value,
            ["DECISIONS_TOOLS_MODEL"] = value,
            ["DECISIONS_TOOLS_MIN_PROBABILITY"] = value,
            ["DECISIONS_TOOLS_MIN_KEEP"] = value,
            ["DECISIONS_TOOLS_ALWAYS_KEEP"] = value,
            ["DECISIONS_TOOLS_STICKY_PER_SESSION"] = value,
            ["DECISIONS_TOOLS_MAX_ATTEMPTS"] = value,
            ["DECISIONS_TOOLS_TIMEOUT"] = value,
            ["DECISIONS_TOOLS_CIRCUIT_BREAK_SECONDS"] = value
        });

        options.DecisionsTools.Enabled.ShouldBeTrue();
        options.DecisionsTools.BaseUrl.ShouldBe("https://decision.example");
        options.DecisionsTools.Path.ShouldBe("/bound");
        options.DecisionsTools.Model.ShouldBe("bound-model");
        options.DecisionsTools.MinProbability.ShouldBe(0.25);
        options.DecisionsTools.MinKeepTools.ShouldBe(7);
        options.DecisionsTools.AlwaysKeep.ShouldBe("bound_tool");
        options.DecisionsTools.StickyPerSession.ShouldBeTrue();
        options.DecisionsTools.MaxAttempts.ShouldBe(2);
        options.DecisionsTools.TimeoutSeconds.ShouldBe(30);
        options.DecisionsTools.CircuitBreakSeconds.ShouldBe(180);
        logger.Entries.ShouldBeEmpty();
    }

    [Fact]
    public void Numeric_overrides_use_invariant_culture_under_a_comma_decimal_locale()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("da-DK");
            var options = new ImposterOptions();

            Apply(options, new Dictionary<string, string?>
            {
                ["DECISIONS_TOOLS_MIN_PROBABILITY"] = "0.50",
                ["DECISIONS_TOOLS_MAX_ATTEMPTS"] = "7",
                ["DECISIONS_TOOLS_TIMEOUT"] = "120",
                ["DECISIONS_TOOLS_CIRCUIT_BREAK_SECONDS"] = "240"
            });

            options.DecisionsTools.MinProbability.ShouldBe(0.50);
            options.DecisionsTools.MaxAttempts.ShouldBe(7);
            options.DecisionsTools.TimeoutSeconds.ShouldBe(120);
            options.DecisionsTools.CircuitBreakSeconds.ShouldBe(240);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Theory]
    [InlineData("DECISIONS_TOOLS_ENABLED", "yes")]
    [InlineData("DECISIONS_TOOLS_MIN_KEEP", "twenty")]
    [InlineData("DECISIONS_TOOLS_MIN_PROBABILITY", "0,50")]
    [InlineData("DECISIONS_TOOLS_MIN_PROBABILITY", "NaN")]
    [InlineData("DECISIONS_TOOLS_MIN_PROBABILITY", "Infinity")]
    [InlineData("DECISIONS_TOOLS_MIN_PROBABILITY", "-Infinity")]
    [InlineData("DECISIONS_TOOLS_MIN_PROBABILITY", "1e500")]
    [InlineData("DECISIONS_TOOLS_MAX_ATTEMPTS", "1.5")]
    [InlineData("DECISIONS_TOOLS_MAX_ATTEMPTS", "2147483648")]
    [InlineData("DECISIONS_TOOLS_TIMEOUT", "one minute")]
    public void Invalid_values_warn_and_preserve_bound_settings(string variable, string value)
    {
        ImposterOptions options = BoundOptions();

        CapturingLogger logger = Apply(options, new Dictionary<string, string?> { [variable] = value });

        options.DecisionsTools.Enabled.ShouldBeTrue();
        options.DecisionsTools.MinProbability.ShouldBe(0.25);
        options.DecisionsTools.MaxAttempts.ShouldBe(2);
        options.DecisionsTools.TimeoutSeconds.ShouldBe(30);
        logger.Entries.ShouldContain(entry => entry.Level == LogLevel.Warning && entry.Message.Contains(variable));
    }

    [Fact]
    public void Parseable_out_of_range_overrides_reach_startup_validation()
    {
        var options = new ImposterOptions
        {
            Providers = { ["default"] = new ProviderOptions { Dialect = "openai", BaseUrl = "https://api.example" } }
        };
        Apply(options, new Dictionary<string, string?>
        {
            ["DECISIONS_TOOLS_ENABLED"] = "true",
            ["DECISIONS_TOOLS_MIN_PROBABILITY"] = "1.5",
            ["DECISIONS_TOOLS_MAX_ATTEMPTS"] = "0",
            ["DECISIONS_TOOLS_TIMEOUT"] = "-5"
        });

        ValidateOptionsResult policy = new ImposterOptionsValidator().Validate(null, options);
        var transport = new DecisionServiceOptions();
        new DecisionServiceOptionsSetup(Options.Create(options)).Configure("Imposter:DecisionsTools", transport);
        ValidateOptionsResult shared = new DecisionServiceOptionsValidator(
            [new DecisionToolsConsumer(Options.Create(options))]).Validate("Imposter:DecisionsTools", transport);

        policy.Failed.ShouldBeTrue();
        policy.FailureMessage.ShouldContain("MinProbability");
        shared.Failed.ShouldBeTrue();
        shared.FailureMessage.ShouldContain("MaxAttempts");
        shared.FailureMessage.ShouldContain("TimeoutSeconds");
    }

    [Fact]
    public void Every_decision_setting_has_a_conventional_variable_that_overrides_it()
    {
        // Drift guard: reflects over the options type, so a new setting without a DECISIONS_TOOLS_* variable
        // fails here instead of shipping without a conventional override (HLD 015 NFR-02).
        PropertyInfo[] properties = typeof(DecisionsToolsOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        ImposterOptionsPostConfigure.DecisionsToolsVariables.Keys
            .ShouldBe(properties.Select(static property => property.Name), ignoreOrder: true);

        foreach (PropertyInfo property in properties)
        {
            var options = new ImposterOptions();
            object? before = property.GetValue(options.DecisionsTools);
            string variable = ImposterOptionsPostConfigure.DecisionsToolsVariables[property.Name];

            Apply(options, new Dictionary<string, string?> { [variable] = DifferentValue(property.PropertyType, before) });

            property.GetValue(options.DecisionsTools).ShouldNotBe(before, $"{variable} should override {property.Name}");
        }
    }

    private static string DifferentValue(Type type, object? current) => type switch
    {
        _ when type == typeof(bool) => (!(bool)current!).ToString(),
        _ when type == typeof(int) => ((int)current! + 1).ToString(CultureInfo.InvariantCulture),
        _ when type == typeof(double) => ((double)current! / 2).ToString(CultureInfo.InvariantCulture),
        _ when type == typeof(string) => current + "-override",
        _ => throw new NotSupportedException($"Add an override value for {type.Name} settings.")
    };

    private static IConfiguration Configuration(IDictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static CapturingLogger Apply(ImposterOptions options, IDictionary<string, string?> values)
    {
        var logger = new CapturingLogger();
        new ImposterOptionsPostConfigure(Configuration(values), logger).PostConfigure(null, options);
        return logger;
    }

    private static ImposterOptions BoundOptions() => new()
    {
        DecisionsTools = new DecisionsToolsOptions
        {
            Enabled = true,
            BaseUrl = "https://decision.example",
            Path = "/bound",
            Model = "bound-model",
            MinProbability = 0.25,
            MinKeepTools = 7,
            AlwaysKeep = "bound_tool",
            StickyPerSession = true,
            MaxAttempts = 2,
            TimeoutSeconds = 30
        }
    };

    private sealed class CapturingLogger : ILogger<ImposterOptionsPostConfigure>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
    }
}
