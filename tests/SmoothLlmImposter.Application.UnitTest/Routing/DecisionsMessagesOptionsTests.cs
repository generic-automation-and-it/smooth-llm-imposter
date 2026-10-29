using System.Globalization;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SmoothLlmImposter.Application.Features.Routing;
using SmoothLlmImposter.Application.Features.Routing.Decisions;

namespace SmoothLlmImposter.Application.UnitTest.Routing;

public class DecisionsMessagesOptionsTests
{
    [Fact]
    public void Defaults_keep_gate_disabled()
    {
        DecisionsMessagesOptions options = new ImposterOptions().DecisionsMessages;

        options.Enabled.ShouldBeFalse();
        options.BaseUrl.ShouldBe("http://host.docker.internal:1234");
        options.Path.ShouldBe("/v1/systemone");
        options.Model.ShouldBe("openjev");
        options.MinProbability.ShouldBe(0.50);
        options.MaxAttempts.ShouldBe(2);
        options.RetryDelaySeconds.ShouldBe(5);
        options.TimeoutSeconds.ShouldBe(60);
        options.CircuitBreakSeconds.ShouldBe(180);
    }

    [Fact]
    public void Every_decision_setting_has_a_conventional_variable_that_overrides_it()
    {
        PropertyInfo[] properties = typeof(DecisionsMessagesOptions).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        ImposterOptionsPostConfigure.DecisionsMessagesVariables.Keys
            .ShouldBe(properties.Select(static property => property.Name), ignoreOrder: true);

        foreach (PropertyInfo property in properties)
        {
            var options = new ImposterOptions();
            object? before = property.GetValue(options.DecisionsMessages);
            string variable = ImposterOptionsPostConfigure.DecisionsMessagesVariables[property.Name];

            // The gate is inert while the optimizer is off (LADR-06), so enable the optimizer for the
            // override check to observe the gate value rather than the forced-off switch.
            Apply(options, new Dictionary<string, string?>
            {
                ["LLM_MESSAGES_ENABLED"] = "true",
                [variable] = DifferentValue(property.PropertyType, before)
            });

            property.GetValue(options.DecisionsMessages).ShouldNotBe(before, $"{variable} should override {property.Name}");
        }
    }

    private static void Apply(ImposterOptions options, Dictionary<string, string?> values)
    {
        var post = new ImposterOptionsPostConfigure(
            new ConfigurationBuilder().AddInMemoryCollection(values!).Build(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ImposterOptionsPostConfigure>.Instance);
        post.PostConfigure(null, options);
    }

    private static string DifferentValue(Type type, object? current) => type switch
    {
        _ when type == typeof(bool) => (!(bool)current!).ToString(),
        _ when type == typeof(double) => ((double)current! / 2).ToString(CultureInfo.InvariantCulture),
        _ when type == typeof(int) => ((int)current! + 1).ToString(CultureInfo.InvariantCulture),
        _ when type == typeof(string) => current + "-override",
        _ => throw new NotSupportedException($"Add an override value for {type.Name} settings.")
    };
}