using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SmoothLlmImposter.Application.Features.Routing;

namespace SmoothLlmImposter.Application.UnitTest.Routing;

public class ImposterOptionsPostConfigureTests
{
    private static (ImposterOptions Options, CapturingLogger Logger) Resolve(
        IDictionary<string, string?> environment,
        string key,
        ProviderOptions provider)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(environment)
            .Build();
        var logger = new CapturingLogger();
        var sut = new ImposterOptionsPostConfigure(configuration, logger);

        var options = new ImposterOptions { Providers = { [key] = provider } };
        sut.PostConfigure(name: null, options);

        return (options, logger);
    }

    [Fact]
    public void Api_key_fills_secret()
    {
        var (options, _) = Resolve(
            new Dictionary<string, string?> { ["OPENCODE_GO_API_KEY"] = "sk-conventional" },
            "opencode-go",
            new ProviderOptions { Dialect = "openai", BaseUrl = "https://o.example" });

        options.Providers["opencode-go"].Secret.ShouldBe("sk-conventional");
    }

    [Fact]
    public void Every_suffix_applies_to_its_mapped_field()
    {
        foreach (ImposterOptionsPostConfigure.ConventionalField field in ImposterOptionsPostConfigure.Fields)
        {
            bool isBool = field.Suffix == "_IS_DEFAULT";
            string value = isBool ? "true" : "v" + field.Suffix;

            var (options, _) = Resolve(
                new Dictionary<string, string?> { ["OPENCODE_GO" + field.Suffix] = value },
                "opencode-go",
                new ProviderOptions { Dialect = "openai", BaseUrl = "https://o.example" });

            PropertyInfo property = typeof(ProviderOptions).GetProperty(field.PropertyName)!;
            object? actual = property.GetValue(options.Providers["opencode-go"]);
            object expected = isBool ? true : value;

            actual.ShouldBe(expected, $"suffix {field.Suffix} should set {field.PropertyName}");
        }
    }

    [Fact]
    public void Match_is_case_insensitive()
    {
        // Env var written in lowercase still resolves the uppercase-normalized prefix (IConfiguration is
        // case-insensitive), which sidesteps the dict-key casing footgun.
        var (options, _) = Resolve(
            new Dictionary<string, string?> { ["opencode_go_api_key"] = "sk-lower" },
            "opencode-go",
            new ProviderOptions { Dialect = "openai", BaseUrl = "https://o.example" });

        options.Providers["opencode-go"].Secret.ShouldBe("sk-lower");
    }

    [Fact]
    public void Conventional_value_wins_over_already_bound_value()
    {
        // The bound value stands in for a structured-env / appsettings value; the conventional var wins.
        var (options, _) = Resolve(
            new Dictionary<string, string?> { ["OPENCODE_GO_API_KEY"] = "sk-conventional" },
            "opencode-go",
            new ProviderOptions { Dialect = "openai", BaseUrl = "https://o.example", Secret = "sk-structured" });

        options.Providers["opencode-go"].Secret.ShouldBe("sk-conventional");
    }

    [Fact]
    public void Absent_conventional_var_leaves_bound_value()
    {
        var (options, _) = Resolve(
            new Dictionary<string, string?>(),
            "opencode-go",
            new ProviderOptions { Dialect = "openai", BaseUrl = "https://o.example", Secret = "sk-bound" });

        options.Providers["opencode-go"].Secret.ShouldBe("sk-bound");
    }

    [Fact]
    public void Resolved_secret_value_is_never_logged()
    {
        const string secret = "sk-super-secret-value";

        var (_, logger) = Resolve(
            new Dictionary<string, string?> { ["OPENCODE_GO_API_KEY"] = secret },
            "opencode-go",
            new ProviderOptions { Dialect = "openai", BaseUrl = "https://o.example" });

        logger.Entries.ShouldNotBeEmpty();
        logger.Entries.ShouldAllBe(entry => !entry.Contains(secret));
        // The variable name + provider may be logged at Debug; the value may not.
        logger.Entries.ShouldContain(entry => entry.Contains("OPENCODE_GO_API_KEY") && entry.Contains("opencode-go"));
    }

    [Theory]
    [InlineData("opencode-go", "OPENCODE_GO")]
    [InlineData("opencode.anthropic", "OPENCODE_ANTHROPIC")]
    [InlineData("OpenCode-Go", "OPENCODE_GO")]
    [InlineData("a--b", "A_B")]
    [InlineData("-x-", "X")]
    [InlineData("openai-default", "OPENAI_DEFAULT")]
    public void Key_normalizes_to_env_prefix(string key, string expectedPrefix) =>
        ImposterOptionsPostConfigure.ToEnvPrefix(key).ShouldBe(expectedPrefix);

    [Fact]
    public void Every_bindable_scalar_field_has_a_mapped_suffix()
    {
        // LADR-02 Open item: a new scalar ProviderOptions field must be added to the suffix map, or it
        // silently lacks a conventional override. Name is the identity (the key), so it is excluded.
        HashSet<string> scalarProperties = typeof(ProviderOptions)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(string) || p.PropertyType == typeof(bool))
            .Select(p => p.Name)
            .ToHashSet();
        scalarProperties.Remove(nameof(ProviderOptions.Name));

        HashSet<string> mapped = ImposterOptionsPostConfigure.Fields
            .Select(f => f.PropertyName)
            .ToHashSet();

        mapped.ShouldBe(scalarProperties, ignoreOrder: true);
    }

    /// <summary>Captures formatted log lines so a test can assert what was (and was not) logged.</summary>
    private sealed class CapturingLogger : ILogger<ImposterOptionsPostConfigure>
    {
        public List<string> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add(formatter(state, exception));
    }
}
