using System.Globalization;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SmoothLlmImposter.Application.Common.Clients.Optimizer;
using SmoothLlmImposter.Application.Features.Routing;

namespace SmoothLlmImposter.Application.UnitTest.Routing;

public sealed class LlmMessagesOptionsTests
{
    [Fact]
    public void Defaults_keep_the_optimizer_disabled()
    {
        LlmMessagesOptions options = new ImposterOptions().LlmMessages;

        options.Enabled.ShouldBeFalse();
        options.Path.ShouldBe("/v1/chat/completions");
        options.MaxAttempts.ShouldBe(3);
        options.TimeoutSeconds.ShouldBe(60);
        options.CircuitBreakSeconds.ShouldBe(180);
        options.CacheTtlHours.ShouldBe(120);
        options.ApiKey.ShouldBeNull();
    }

    [Fact]
    public void Every_optimizer_setting_has_a_conventional_variable_that_overrides_it()
    {
        PropertyInfo[] properties = typeof(LlmMessagesOptions)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance);
        ImposterOptionsPostConfigure.LlmMessagesVariables.Keys
            .ShouldBe(properties.Select(static property => property.Name), ignoreOrder: true);

        foreach (PropertyInfo property in properties)
        {
            var options = new ImposterOptions();
            object? before = property.GetValue(options.LlmMessages);
            string variable = ImposterOptionsPostConfigure.LlmMessagesVariables[property.Name];

            Apply(options, new Dictionary<string, string?> { [variable] = DifferentValue(property.PropertyType, before) });

            property.GetValue(options.LlmMessages).ShouldNotBe(before, $"{variable} should override {property.Name}");
        }
    }

    [Theory]
    [InlineData(false, null, false)]
    [InlineData(true, null, true)]
    [InlineData(true, "true", true)]
    [InlineData(true, "false", false)]
    [InlineData(false, "true", false)]
    public void Switch_table_controls_the_gate(bool optimizerEnabled, string? gate, bool expectedGate)
    {
        var options = new ImposterOptions();
        var values = new Dictionary<string, string?> { ["LLM_MESSAGES_ENABLED"] = optimizerEnabled.ToString() };
        if (gate is not null)
        {
            values["DECISIONS_MESSAGES_ENABLED"] = gate;
        }

        Apply(options, values);

        options.DecisionsMessages.Enabled.ShouldBe(expectedGate);
    }

    private static void Apply(ImposterOptions options, Dictionary<string, string?> values)
    {
        var post = new ImposterOptionsPostConfigure(
            new ConfigurationBuilder().AddInMemoryCollection(values!).Build(),
            NullLogger<ImposterOptionsPostConfigure>.Instance);
        post.PostConfigure(null, options);
    }

    private static string DifferentValue(Type type, object? current) => type switch
    {
        _ when type == typeof(bool) => (!(bool)current!).ToString(),
        _ when type == typeof(int) => ((int)current! + 1).ToString(CultureInfo.InvariantCulture),
        _ when type == typeof(string) => current is null ? "value" : current + "-x",
        _ => throw new NotSupportedException($"Add an override value for {type.Name} settings.")
    };
}
