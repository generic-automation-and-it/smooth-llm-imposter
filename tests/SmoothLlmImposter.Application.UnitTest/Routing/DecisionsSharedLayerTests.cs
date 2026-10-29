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

/// <summary>
/// HLD 017 LADR-06 / HLD 015 LADR-05: every decision section field resolves section value → shared
/// <c>DECISIONS_*</c> → built-in default, for structured and conventional configuration alike.
/// Options are built through the real binder plus the post-configure step, so structured keys and the
/// configuration the post-configure step reads are always the same source.
/// </summary>
public class DecisionsSharedLayerTests
{
    private const string Tools = nameof(ImposterOptions.DecisionsTools);
    private const string Messages = nameof(ImposterOptions.DecisionsMessages);

    private static readonly string[] BothSections =
    [
        nameof(DecisionsOptions.BaseUrl),
        nameof(DecisionsOptions.Path),
        nameof(DecisionsOptions.Model),
        nameof(DecisionsOptions.MinProbability),
        nameof(DecisionsOptions.MaxAttempts),
        nameof(DecisionsOptions.TimeoutSeconds),
        nameof(DecisionsOptions.CircuitBreakSeconds),
    ];

    private static readonly string[] ToolSectionOnly =
    [
        nameof(DecisionsOptions.MinKeepTools),
        nameof(DecisionsOptions.AlwaysKeep),
        nameof(DecisionsOptions.StickyPerSession),
    ];

    private enum Layer
    {
        SharedStructured,
        SharedConventional,
        SectionStructured,
        SectionConventional,
    }

    [Fact]
    public void Shared_layer_is_unset_by_default()
    {
        DecisionsOptions shared = new ImposterOptions().Decisions;

        foreach (PropertyInfo property in SharedProperties())
        {
            property.GetValue(shared).ShouldBeNull(property.Name);
        }
    }

    [Fact]
    public void Shared_layer_has_no_enable_switch_and_every_property_targets_its_sections()
    {
        // Drift guard: a shared setting must name a conventional variable, exist on the tool section, and
        // reach the message section exactly when it is a both-section setting (LADR-06).
        string[] names = SharedProperties().Select(static property => property.Name).ToArray();

        names.ShouldBe(BothSections.Concat(ToolSectionOnly).ToArray(), ignoreOrder: true);
        names.ShouldNotContain("Enabled");
        ImposterOptionsPostConfigure.DecisionsVariables.Keys.ShouldBe(names, ignoreOrder: true);
        ImposterOptionsPostConfigure.DecisionsVariables.Values.ShouldAllBe(variable => variable.StartsWith("DECISIONS_") && !variable.StartsWith("DECISIONS_TOOLS_") && !variable.StartsWith("DECISIONS_MESSAGES_"));
        ImposterOptionsPostConfigure.DecisionsVariables.Values.ShouldNotContain("DECISIONS_ENABLED");

        foreach (string name in names)
        {
            typeof(DecisionsToolsOptions).GetProperty(name).ShouldNotBeNull($"{name} has no tool-section target");
            (typeof(DecisionsMessagesOptions).GetProperty(name) is not null).ShouldBe(BothSections.Contains(name), name);
        }
    }

    [Fact]
    public void Message_section_carries_its_own_transport_variables()
    {
        string[] transport = ["BaseUrl", "Path", "Model", "MaxAttempts", "TimeoutSeconds"];

        foreach (string name in transport)
        {
            ImposterOptionsPostConfigure.DecisionsMessagesVariables[name].ShouldStartWith("DECISIONS_MESSAGES_");
        }

        ImposterOptionsPostConfigure.DecisionsMessagesVariables[nameof(DecisionsMessagesOptions.TimeoutSeconds)]
            .ShouldBe("DECISIONS_MESSAGES_TIMEOUT");
    }

    [Theory]
    [MemberData(nameof(SectionFields))]
    public void Every_shared_variable_fills_each_section_it_targets(string section, string property)
    {
        // Drift guard over the applied surface: a shared property without a layering call fails here.
        foreach (Layer layer in new[] { Layer.SharedConventional, Layer.SharedStructured })
        {
            object? defaultValue = DefaultOf(section, property);
            string value = DistinctValue(PropertyType(section, property), layer, defaultValue);

            object? resolved = Resolve(section, property, new Dictionary<string, string?> { [Key(section, property, layer)] = value });

            resolved.ShouldNotBe(defaultValue, $"{layer} {Key(section, property, layer)} should fill {section}.{property}");
            resolved.ShouldBe(Parse(PropertyType(section, property), value));
        }
    }

    public static TheoryData<string, string, string> PrecedenceCases()
    {
        var data = new TheoryData<string, string, string>();
        foreach ((string section, string property) in SectionFieldPairs())
        {
            foreach (string scenario in Scenarios.Keys)
            {
                data.Add(section, property, scenario);
            }
        }

        return data;
    }

    private static readonly Dictionary<string, (Layer[] Present, Layer? Winner)> Scenarios = new()
    {
        ["nothing set → built-in default"] = ([], null),
        ["shared structured only"] = ([Layer.SharedStructured], Layer.SharedStructured),
        ["shared conventional only"] = ([Layer.SharedConventional], Layer.SharedConventional),
        ["shared conventional beats shared structured"] = ([Layer.SharedStructured, Layer.SharedConventional], Layer.SharedConventional),
        ["section structured beats both shared"] = ([Layer.SharedStructured, Layer.SharedConventional, Layer.SectionStructured], Layer.SectionStructured),
        ["section conventional beats everything"] = ([Layer.SharedStructured, Layer.SharedConventional, Layer.SectionStructured, Layer.SectionConventional], Layer.SectionConventional),
        ["section conventional beats shared conventional"] = ([Layer.SharedConventional, Layer.SectionConventional], Layer.SectionConventional),
    };

    [Theory]
    [MemberData(nameof(PrecedenceCases))]
    public void Section_then_shared_then_default_precedence(string section, string property, string scenario)
    {
        (Layer[] present, Layer? winner) = Scenarios[scenario];
        Type type = PropertyType(section, property);
        object? defaultValue = DefaultOf(section, property);
        var values = new Dictionary<string, string?>();
        foreach (Layer layer in present)
        {
            values[Key(section, property, layer)] = type == typeof(bool)
                ? (layer == winner ? "true" : "false")
                : DistinctValue(type, layer, defaultValue);
        }

        object? resolved = Resolve(section, property, values);

        object? expected = winner is { } win ? Parse(type, values[Key(section, property, win)]!) : defaultValue;
        resolved.ShouldBe(expected, $"{section}.{property}: {scenario}");
    }

    [Theory]
    [MemberData(nameof(SectionFields))]
    public void Blank_values_are_unset_at_every_layer(string section, string property)
    {
        Type type = PropertyType(section, property);
        object? defaultValue = DefaultOf(section, property);
        string shared = type == typeof(bool) ? "true" : DistinctValue(type, Layer.SharedStructured, defaultValue);

        var values = new Dictionary<string, string?>
        {
            [Key(section, property, Layer.SharedStructured)] = shared,
            [Key(section, property, Layer.SharedConventional)] = "  ",
            [Key(section, property, Layer.SectionConventional)] = " ",
        };

        // The binder itself rejects a blank structured number or boolean, so only a string section key can be blank.
        if (type == typeof(string))
        {
            values[Key(section, property, Layer.SectionStructured)] = "";
        }

        object? resolved = Resolve(section, property, values);

        resolved.ShouldBe(Parse(type, shared));
    }

    [Theory]
    [MemberData(nameof(ParsedSectionFields))]
    public void Unparseable_section_variable_falls_through_to_shared(string section, string property)
    {
        Type type = PropertyType(section, property);
        object? defaultValue = DefaultOf(section, property);
        string shared = type == typeof(bool) ? "true" : DistinctValue(type, Layer.SharedConventional, defaultValue);

        object? resolved = Resolve(section, property, new Dictionary<string, string?>
        {
            [Key(section, property, Layer.SharedConventional)] = shared,
            [Key(section, property, Layer.SectionConventional)] = "not-a-value",
        });

        resolved.ShouldBe(Parse(type, shared));
    }

    [Theory]
    [MemberData(nameof(BothSectionFields))]
    public void A_section_value_never_reaches_the_other_section(string property)
    {
        // HLD 015 LADR-05: a missing section value never inherits another section's value.
        foreach ((string set, string other) in new[] { (Tools, Messages), (Messages, Tools) })
        {
            object? defaultValue = DefaultOf(other, property);
            string value = DistinctValue(PropertyType(set, property), Layer.SectionConventional, DefaultOf(set, property));

            ImposterOptions options = Build(new Dictionary<string, string?>
            {
                [Key(set, property, Layer.SectionConventional)] = value,
                [Key(set, property, Layer.SectionStructured)] = value,
            });

            SectionOf(options, other).GetType().GetProperty(property)!.GetValue(SectionOf(options, other))
                .ShouldBe(defaultValue, $"{set}.{property} leaked into {other}");
        }
    }

    [Fact]
    public void Decisions_enabled_is_not_a_switch()
    {
        ImposterOptions options = Build(new Dictionary<string, string?>
        {
            ["DECISIONS_ENABLED"] = "true",
            ["Imposter:Decisions:Enabled"] = "true",
            ["LLM_MESSAGES_ENABLED"] = "false",
        });

        options.DecisionsTools.Enabled.ShouldBeFalse();
        options.DecisionsMessages.Enabled.ShouldBeFalse();
    }

    public static TheoryData<string, Dictionary<string, string?>> ToolsOnlyDeployments() => new()
    {
        { "no decision settings", new Dictionary<string, string?>() },
        {
            "every DECISIONS_TOOLS_* variable", new Dictionary<string, string?>
            {
                ["DECISIONS_TOOLS_ENABLED"] = "true",
                ["DECISIONS_TOOLS_BASE_URL"] = "http://127.0.0.1:4321",
                ["DECISIONS_TOOLS_PATH"] = "/classify",
                ["DECISIONS_TOOLS_MODEL"] = "kev-4b",
                ["DECISIONS_TOOLS_MIN_PROBABILITY"] = "0.75",
                ["DECISIONS_TOOLS_MIN_KEEP"] = "12",
                ["DECISIONS_TOOLS_ALWAYS_KEEP"] = "shell,apply_patch",
                ["DECISIONS_TOOLS_STICKY_PER_SESSION"] = "true",
                ["DECISIONS_TOOLS_MAX_ATTEMPTS"] = "5",
                ["DECISIONS_TOOLS_TIMEOUT"] = "90",
                ["DECISIONS_TOOLS_CIRCUIT_BREAK_SECONDS"] = "45",
            }
        },
        {
            "every structured Imposter:DecisionsTools key", new Dictionary<string, string?>
            {
                ["Imposter:DecisionsTools:Enabled"] = "true",
                ["Imposter:DecisionsTools:BaseUrl"] = "http://127.0.0.1:4321",
                ["Imposter:DecisionsTools:Path"] = "/classify",
                ["Imposter:DecisionsTools:Model"] = "kev-4b",
                ["Imposter:DecisionsTools:MinProbability"] = "0.75",
                ["Imposter:DecisionsTools:MinKeepTools"] = "12",
                ["Imposter:DecisionsTools:AlwaysKeep"] = "shell,apply_patch",
                ["Imposter:DecisionsTools:StickyPerSession"] = "true",
                ["Imposter:DecisionsTools:MaxAttempts"] = "5",
                ["Imposter:DecisionsTools:TimeoutSeconds"] = "90",
                ["Imposter:DecisionsTools:CircuitBreakSeconds"] = "45",
            }
        },
        {
            "conventional over structured, mixed", new Dictionary<string, string?>
            {
                ["Imposter:DecisionsTools:Enabled"] = "false",
                ["DECISIONS_TOOLS_ENABLED"] = "true",
                ["Imposter:DecisionsTools:BaseUrl"] = "http://ignored.example",
                ["DECISIONS_TOOLS_BASE_URL"] = "http://127.0.0.1:4321",
                ["Imposter:DecisionsTools:Path"] = "/classify",
                ["DECISIONS_TOOLS_MODEL"] = "kev-4b",
                ["Imposter:DecisionsTools:MinProbability"] = "0.75",
                ["DECISIONS_TOOLS_MIN_KEEP"] = "12",
                ["Imposter:DecisionsTools:AlwaysKeep"] = "shell,apply_patch",
                ["DECISIONS_TOOLS_STICKY_PER_SESSION"] = "true",
                ["Imposter:DecisionsTools:MaxAttempts"] = "5",
                ["DECISIONS_TOOLS_TIMEOUT"] = "90",
                ["Imposter:DecisionsTools:CircuitBreakSeconds"] = "45",
            }
        },
        {
            "invalid conventional values keep the structured value", new Dictionary<string, string?>
            {
                ["Imposter:DecisionsTools:Enabled"] = "true",
                ["DECISIONS_TOOLS_ENABLED"] = "yes",
                ["Imposter:DecisionsTools:BaseUrl"] = "http://127.0.0.1:4321",
                ["Imposter:DecisionsTools:Path"] = "/classify",
                ["Imposter:DecisionsTools:Model"] = "kev-4b",
                ["Imposter:DecisionsTools:MinProbability"] = "0.75",
                ["DECISIONS_TOOLS_MIN_PROBABILITY"] = "0,9",
                ["Imposter:DecisionsTools:MinKeepTools"] = "12",
                ["Imposter:DecisionsTools:AlwaysKeep"] = "shell,apply_patch",
                ["Imposter:DecisionsTools:StickyPerSession"] = "true",
                ["Imposter:DecisionsTools:MaxAttempts"] = "5",
                ["DECISIONS_TOOLS_MAX_ATTEMPTS"] = "1.5",
                ["Imposter:DecisionsTools:TimeoutSeconds"] = "90",
                ["DECISIONS_TOOLS_TIMEOUT"] = "one minute",
                ["Imposter:DecisionsTools:CircuitBreakSeconds"] = "45",
            }
        },
    };

    [Theory]
    [MemberData(nameof(ToolsOnlyDeployments))]
    public void Tools_only_deployments_resolve_as_before_the_shared_layer(string deployment, Dictionary<string, string?> values)
    {
        // Regression table (AC3): expected values are the effective values on main at 0784ee3, where only
        // the DECISIONS_TOOLS_* surface existed. The message section keeps its pre-existing values and its new
        // transport fields stay at the built-in defaults instead of copying the tool section.
        bool configured = values.Count > 0;
        ImposterOptions options = Build(values);
        DecisionsToolsOptions tools = options.DecisionsTools;

        tools.Enabled.ShouldBe(configured, deployment);
        tools.BaseUrl.ShouldBe(configured ? "http://127.0.0.1:4321" : "http://host.docker.internal:1234", deployment);
        tools.Path.ShouldBe(configured ? "/classify" : "/v1/systemone", deployment);
        tools.Model.ShouldBe(configured ? "kev-4b" : "openjev", deployment);
        tools.MinProbability.ShouldBe(configured ? 0.75 : 0.50, deployment);
        tools.MinKeepTools.ShouldBe(configured ? 12 : 20, deployment);
        tools.AlwaysKeep.ShouldBe(configured ? "shell,apply_patch" : "Bash,Read,Edit,Write,Skill", deployment);
        tools.StickyPerSession.ShouldBe(configured, deployment);
        tools.MaxAttempts.ShouldBe(configured ? 5 : 3, deployment);
        tools.TimeoutSeconds.ShouldBe(configured ? 90 : 60, deployment);
        tools.CircuitBreakSeconds.ShouldBe(configured ? 45 : 180, deployment);

        var transport = new DecisionServiceOptions();
        new DecisionServiceOptionsSetup(Microsoft.Extensions.Options.Options.Create(options)).Configure("Imposter:DecisionsTools", transport);
        transport.BaseUrl.ShouldBe(tools.BaseUrl);
        transport.Path.ShouldBe(tools.Path);
        transport.Model.ShouldBe(tools.Model);
        transport.MaxAttempts.ShouldBe(tools.MaxAttempts);
        transport.TimeoutSeconds.ShouldBe(tools.TimeoutSeconds);

        DecisionsMessagesOptions messages = options.DecisionsMessages;
        messages.Enabled.ShouldBeFalse();
        messages.MinProbability.ShouldBe(0.50);
        messages.CircuitBreakSeconds.ShouldBe(180);
        messages.BaseUrl.ShouldBe(DecisionServiceOptions.DefaultBaseUrl);
        messages.Path.ShouldBe(DecisionServiceOptions.DefaultPath);
        messages.Model.ShouldBe(DecisionServiceOptions.DefaultModel);
        messages.MaxAttempts.ShouldBe(DecisionServiceOptions.DefaultMaxAttempts);
        messages.TimeoutSeconds.ShouldBe(DecisionServiceOptions.DefaultTimeoutSeconds);
    }

    [Fact]
    public void Shared_values_reach_the_shared_transport_profile_through_the_tool_section()
    {
        ImposterOptions options = Build(new Dictionary<string, string?>
        {
            ["DECISIONS_BASE_URL"] = "http://shared.example:9000",
            ["DECISIONS_MODEL"] = "shared-model",
            ["DECISIONS_TOOLS_MODEL"] = "tool-model",
        });

        var transport = new DecisionServiceOptions();
        new DecisionServiceOptionsSetup(Microsoft.Extensions.Options.Options.Create(options)).Configure("Imposter:DecisionsTools", transport);

        transport.BaseUrl.ShouldBe("http://shared.example:9000");
        transport.Model.ShouldBe("tool-model");
    }

    [Fact]
    public void Each_section_resolves_its_own_transport_profile_and_never_inherits_the_other()
    {
        // HLD 015 LADR-05: section, then shared DECISIONS_*, then default — never another section's value.
        ImposterOptions options = Build(new Dictionary<string, string?>
        {
            ["DECISIONS_TIMEOUT"] = "45",
            ["DECISIONS_TOOLS_BASE_URL"] = "http://tools.example",
            ["DECISIONS_TOOLS_MODEL"] = "tool-model",
            ["DECISIONS_TOOLS_MAX_ATTEMPTS"] = "5",
            ["DECISIONS_MESSAGES_PATH"] = "/v1/gate",
            ["DECISIONS_MESSAGES_MODEL"] = "gate-model",
        });
        var setup = new DecisionServiceOptionsSetup(Microsoft.Extensions.Options.Options.Create(options));
        var tools = new DecisionServiceOptions();
        var messages = new DecisionServiceOptions();
        var unnamed = new DecisionServiceOptions();

        setup.Configure("Imposter:DecisionsTools", tools);
        setup.Configure("Imposter:DecisionsMessages", messages);
        setup.Configure(unnamed);

        tools.Endpoint.ShouldBe("http://tools.example/v1/systemone");
        tools.Model.ShouldBe("tool-model");
        tools.MaxAttempts.ShouldBe(5);
        tools.TimeoutSeconds.ShouldBe(45);
        messages.Endpoint.ShouldBe(DecisionServiceOptions.DefaultBaseUrl + "/v1/gate");
        messages.Model.ShouldBe("gate-model");
        messages.MaxAttempts.ShouldBe(DecisionServiceOptions.DefaultMaxAttempts);
        messages.TimeoutSeconds.ShouldBe(45);
        unnamed.Endpoint.ShouldBe(DecisionServiceOptions.DefaultBaseUrl + DecisionServiceOptions.DefaultPath);
    }

    [Theory]
    [InlineData("DECISIONS_MESSAGES_BASE_URL", "not-a-url", "Imposter:DecisionsMessages:BaseUrl")]
    [InlineData("DECISIONS_BASE_URL", "ftp://shared.example", "Imposter:DecisionsMessages:BaseUrl")]
    [InlineData("DECISIONS_MESSAGES_PATH", "relative", "Imposter:DecisionsMessages:Path")]
    [InlineData("DECISIONS_MESSAGES_MAX_ATTEMPTS", "0", "Imposter:DecisionsMessages:MaxAttempts")]
    [InlineData("DECISIONS_MAX_ATTEMPTS", "11", "Imposter:DecisionsMessages:MaxAttempts")]
    [InlineData("DECISIONS_MESSAGES_TIMEOUT", "3601", "Imposter:DecisionsMessages:TimeoutSeconds")]
    [InlineData("DECISIONS_TIMEOUT", "0", "Imposter:DecisionsMessages:TimeoutSeconds")]
    [InlineData("Imposter:DecisionsMessages:Model", " ", "Imposter:DecisionsMessages:Model must be non-empty.")]
    [InlineData("DECISIONS_MODEL", "shared-model", null)]
    public void Message_gate_on_validates_its_resolved_transport(string key, string value, string? failure)
    {
        ImposterOptions options = Build(new Dictionary<string, string?>
        {
            ["LLM_MESSAGES_ENABLED"] = "true",
            ["LLM_MESSAGES_BASE_URL"] = "http://optimizer.example",
            ["LLM_MESSAGES_MODEL"] = "optimizer",
            [key] = value,
        });

        ValidateOptionsResult result = new ImposterOptionsValidator().Validate(null, WithProvider(options));

        if (failure is null)
        {
            result.Succeeded.ShouldBeTrue(result.FailureMessage);
            return;
        }

        result.Failed.ShouldBeTrue();
        result.FailureMessage!.ShouldContain(failure);
    }

    [Theory]
    [InlineData("false", null)]
    [InlineData("true", "false")]
    public void Message_transport_is_not_validated_while_the_gate_is_off(string optimizer, string? gate)
    {
        ImposterOptions options = Build(new Dictionary<string, string?>
        {
            ["LLM_MESSAGES_ENABLED"] = optimizer,
            ["LLM_MESSAGES_BASE_URL"] = "http://optimizer.example",
            ["LLM_MESSAGES_MODEL"] = "optimizer",
            ["DECISIONS_MESSAGES_ENABLED"] = gate,
            ["DECISIONS_MESSAGES_BASE_URL"] = "not-a-url",
            ["DECISIONS_MESSAGES_PATH"] = "relative",
            ["DECISIONS_MESSAGES_MAX_ATTEMPTS"] = "0",
            ["DECISIONS_MESSAGES_TIMEOUT"] = "0",
        });

        ValidateOptionsResult result = new ImposterOptionsValidator().Validate(null, WithProvider(options));

        result.Succeeded.ShouldBeTrue(result.FailureMessage);
    }

    [Fact]
    public void Shared_values_are_not_validated_while_every_consumer_is_off()
    {
        ImposterOptions options = Build(new Dictionary<string, string?>
        {
            ["DECISIONS_BASE_URL"] = "not-a-url",
            ["DECISIONS_PATH"] = "relative",
            ["DECISIONS_MIN_PROBABILITY"] = "2",
            ["DECISIONS_MAX_ATTEMPTS"] = "0",
            ["DECISIONS_TIMEOUT"] = "0",
            ["DECISIONS_CIRCUIT_BREAK_SECONDS"] = "0",
            ["DECISIONS_MIN_KEEP"] = "-1",
        });

        new ImposterOptionsValidator().Validate(null, WithProvider(options)).Succeeded.ShouldBeTrue();
        var transport = new DecisionServiceOptions();
        new DecisionServiceOptionsSetup(Microsoft.Extensions.Options.Options.Create(options)).Configure("Imposter:DecisionsTools", transport);
        new DecisionServiceOptionsValidator([new DecisionToolsConsumer(Microsoft.Extensions.Options.Options.Create(options))])
            .Validate("Imposter:DecisionsTools", transport).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Invalid_shared_values_fail_the_enabled_tool_section()
    {
        ImposterOptions options = Build(new Dictionary<string, string?>
        {
            ["DECISIONS_TOOLS_ENABLED"] = "true",
            ["DECISIONS_BASE_URL"] = "not-a-url",
            ["DECISIONS_MIN_PROBABILITY"] = "2",
            ["DECISIONS_MIN_KEEP"] = "-1",
        });

        ValidateOptionsResult policy = new ImposterOptionsValidator().Validate(null, WithProvider(options));
        var transport = new DecisionServiceOptions();
        new DecisionServiceOptionsSetup(Microsoft.Extensions.Options.Options.Create(options)).Configure("Imposter:DecisionsTools", transport);
        ValidateOptionsResult shared = new DecisionServiceOptionsValidator(
            [new DecisionToolsConsumer(Microsoft.Extensions.Options.Options.Create(options))]).Validate("Imposter:DecisionsTools", transport);

        policy.FailureMessage!.ShouldContain("Imposter:DecisionsTools:MinProbability");
        policy.FailureMessage!.ShouldContain("Imposter:DecisionsTools:MinKeepTools");
        shared.FailureMessage!.ShouldContain("BaseUrl must be an absolute HTTP(S) URL.");
    }

    public static TheoryData<string, string> SectionFields()
    {
        var data = new TheoryData<string, string>();
        foreach ((string section, string property) in SectionFieldPairs())
        {
            data.Add(section, property);
        }

        return data;
    }

    public static TheoryData<string> BothSectionFields() => new(BothSections);

    public static TheoryData<string, string> ParsedSectionFields()
    {
        var data = new TheoryData<string, string>();
        foreach ((string section, string property) in SectionFieldPairs().Where(static pair => PropertyType(pair.Section, pair.Property) != typeof(string)))
        {
            data.Add(section, property);
        }

        return data;
    }

    private static IEnumerable<(string Section, string Property)> SectionFieldPairs() =>
        BothSections.SelectMany(static property => new[] { (Tools, property), (Messages, property) })
            .Concat(ToolSectionOnly.Select(static property => (Tools, property)));

    private static PropertyInfo[] SharedProperties() =>
        typeof(DecisionsOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance);

    private static string Key(string section, string property, Layer layer) => layer switch
    {
        Layer.SharedStructured => $"Imposter:Decisions:{property}",
        Layer.SharedConventional => ImposterOptionsPostConfigure.DecisionsVariables[property],
        Layer.SectionStructured => $"Imposter:{section}:{property}",
        Layer.SectionConventional => SectionVariables(section)[property],
        _ => throw new ArgumentOutOfRangeException(nameof(layer)),
    };

    private static IReadOnlyDictionary<string, string> SectionVariables(string section) => section == Tools
        ? ImposterOptionsPostConfigure.DecisionsToolsVariables
        : ImposterOptionsPostConfigure.DecisionsMessagesVariables;

    private static object SectionOf(ImposterOptions options, string section) => section == Tools
        ? options.DecisionsTools
        : options.DecisionsMessages;

    private static Type PropertyType(string section, string property) =>
        SectionOf(new ImposterOptions(), section).GetType().GetProperty(property)!.PropertyType;

    private static object? DefaultOf(string section, string property)
    {
        object target = SectionOf(new ImposterOptions(), section);
        return target.GetType().GetProperty(property)!.GetValue(target);
    }

    private static string DistinctValue(Type type, Layer layer, object? defaultValue)
    {
        int step = (int)layer + 1;
        return type switch
        {
            _ when type == typeof(string) => $"{defaultValue}-{layer}",
            _ when type == typeof(int) => ((int)defaultValue! + step).ToString(CultureInfo.InvariantCulture),
            _ when type == typeof(double) => (step / 10.0).ToString(CultureInfo.InvariantCulture),
            _ when type == typeof(bool) => (!(bool)defaultValue!).ToString(),
            _ => throw new NotSupportedException($"Add a layered value for {type.Name} settings."),
        };
    }

    private static object Parse(Type type, string value) => type switch
    {
        _ when type == typeof(string) => value,
        _ when type == typeof(int) => int.Parse(value, CultureInfo.InvariantCulture),
        _ when type == typeof(double) => double.Parse(value, CultureInfo.InvariantCulture),
        _ when type == typeof(bool) => bool.Parse(value),
        _ => throw new NotSupportedException(type.Name),
    };

    private static object? Resolve(string section, string property, Dictionary<string, string?> values)
    {
        object target = SectionOf(Build(values), section);
        return target.GetType().GetProperty(property)!.GetValue(target);
    }

    [Theory]
    [InlineData("DECISIONS_TOOLS_BASE_URL", "http://user:secret@tools.example:4321?key=hidden", null, null, true)]
    [InlineData("Imposter:DecisionsTools:Model", "tool-model", null, null, true)]
    [InlineData("DECISIONS_TOOLS_PATH", "/classify", null, null, true)]
    [InlineData("DECISIONS_TOOLS_MODEL", "tool-model", "DECISIONS_MODEL", "shared-model", false)]
    [InlineData("DECISIONS_TOOLS_MODEL", "tool-model", "DECISIONS_BASE_URL", "http://shared.example", false)]
    [InlineData("DECISIONS_TOOLS_MODEL", "tool-model", "DECISIONS_MESSAGES_MODEL", "gate-model", false)]
    [InlineData("DECISIONS_TOOLS_MODEL", "tool-model", "Imposter:DecisionsMessages:BaseUrl", "http://gate.example", false)]
    [InlineData("DECISIONS_TOOLS_MODEL", "tool-model", "LLM_MESSAGES_ENABLED", "false", false)]
    [InlineData("DECISIONS_TOOLS_MODEL", "tool-model", "DECISIONS_MESSAGES_ENABLED", "false", false)]
    [InlineData("DECISIONS_TOOLS_MODEL", DecisionServiceOptions.DefaultModel, null, null, false)]
    [InlineData("DECISIONS_TOOLS_TIMEOUT", "30", null, null, false)]
    public async Task Warns_once_when_only_tool_transport_moves_the_message_gate_to_the_default_classifier(
        string toolKey, string toolValue, string? otherKey, string? otherValue, bool warns)
    {
        // WT-23: the specified precedence sends the message gate to the default classifier when only
        // DECISIONS_TOOLS_* transport is set; the deployment learns that from one startup warning.
        Dictionary<string, string?> values = new()
        {
            ["LLM_MESSAGES_ENABLED"] = "true",
            [toolKey] = toolValue,
        };
        if (otherKey is not null)
        {
            values[otherKey] = otherValue;
        }

        var logger = new WarningLogger();
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        await using ServiceProvider services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(configuration)
            .Configure<ImposterOptions>(configuration.GetSection(ImposterOptions.SectionName))
            .AddSingleton<IPostConfigureOptions<ImposterOptions>, ImposterOptionsPostConfigure>()
            .BuildServiceProvider();
        IOptions<ImposterOptions> options = services.GetRequiredService<IOptions<ImposterOptions>>();
        _ = services.GetRequiredService<IOptionsMonitor<ImposterOptions>>().CurrentValue;

        await new MessageGateTransportWarning(options, configuration, logger).StartAsync(TestContext.Current.CancellationToken);

        string[] warnings = logger.Warnings.Where(warning => warning.Contains("message gate", StringComparison.Ordinal)).ToArray();
        if (!warns)
        {
            warnings.ShouldBeEmpty();
            return;
        }

        warnings.Length.ShouldBe(1);
        warnings[0].ShouldContain("DECISIONS_TOOLS_*");
        warnings[0].ShouldContain(DecisionServiceOptions.DefaultBaseUrl + DecisionServiceOptions.DefaultPath);
        warnings[0].ShouldNotContain("secret");
        warnings[0].ShouldNotContain("hidden");
    }

    private sealed class WarningLogger : ILogger<MessageGateTransportWarning>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }

    private static ImposterOptions Build(Dictionary<string, string?> values)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        using ServiceProvider services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(configuration)
            .Configure<ImposterOptions>(configuration.GetSection(ImposterOptions.SectionName))
            .AddSingleton<IPostConfigureOptions<ImposterOptions>, ImposterOptionsPostConfigure>()
            .BuildServiceProvider();

        return services.GetRequiredService<IOptions<ImposterOptions>>().Value;
    }

    private static ImposterOptions WithProvider(ImposterOptions options)
    {
        options.Providers["default"] = new ProviderOptions { Dialect = "openai", BaseUrl = "https://api.example" };
        return options;
    }
}
