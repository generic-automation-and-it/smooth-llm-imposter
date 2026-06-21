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
    public void Authorization_bearer_suffix_fills_secret()
    {
        // The auth-typed secret alias: a Bearer subscription token reads as <NAME>_AUTHORIZATION_BEARER
        // and fills the same Secret slot as _API_KEY (the motivating case is the personal providers).
        var (options, _) = Resolve(
            new Dictionary<string, string?> { ["ANTHROPIC_PERSONAL_AUTHORIZATION_BEARER"] = "sk-personal-sub" },
            "anthropic-personal",
            new ProviderOptions { Dialect = "anthropic", BaseUrl = "https://api.anthropic.com", AuthScheme = "Bearer" });

        options.Providers["anthropic-personal"].Secret.ShouldBe("sk-personal-sub");
    }

    [Fact]
    public void Api_key_wins_when_both_secret_suffixes_are_set()
    {
        // First-present-wins: _API_KEY is canonical and applied first, so the _AUTHORIZATION_BEARER alias
        // never clobbers it when an operator (mis)configures both for one provider.
        var (options, _) = Resolve(
            new Dictionary<string, string?>
            {
                ["ANTHROPIC_PERSONAL_API_KEY"] = "sk-canonical",
                ["ANTHROPIC_PERSONAL_AUTHORIZATION_BEARER"] = "sk-alias"
            },
            "anthropic-personal",
            new ProviderOptions { Dialect = "anthropic", BaseUrl = "https://api.anthropic.com", AuthScheme = "Bearer" });

        options.Providers["anthropic-personal"].Secret.ShouldBe("sk-canonical");
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
    public void Dialect_suffixed_provider_can_share_base_provider_api_key()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OPENCODE_GO_API_KEY"] = "sk-opencode",
                ["OPENROUTER_API_KEY"] = "sk-openrouter"
            })
            .Build();
        var logger = new CapturingLogger();
        var sut = new ImposterOptionsPostConfigure(configuration, logger);

        var options = new ImposterOptions
        {
            Providers =
            {
                ["opencode-go-openai"] = new ProviderOptions { Dialect = "openai", BaseUrl = "https://opencode.example", AuthScheme = "Bearer" },
                ["opencode-go-anthropic"] = new ProviderOptions { Dialect = "anthropic", BaseUrl = "https://opencode.example", AuthScheme = "ApiKey" },
                ["openrouter-openai"] = new ProviderOptions { Dialect = "openai", BaseUrl = "https://openrouter.example", AuthScheme = "Bearer" },
                ["openrouter-anthropic"] = new ProviderOptions { Dialect = "anthropic", BaseUrl = "https://openrouter.example", AuthScheme = "Bearer" }
            }
        };

        sut.PostConfigure(name: null, options);

        options.Providers["opencode-go-openai"].Secret.ShouldBe("sk-opencode");
        options.Providers["opencode-go-anthropic"].Secret.ShouldBe("sk-opencode");
        options.Providers["openrouter-openai"].Secret.ShouldBe("sk-openrouter");
        options.Providers["openrouter-openai"].AuthScheme.ShouldBe("Bearer");
        options.Providers["openrouter-anthropic"].Secret.ShouldBe("sk-openrouter");
        options.Providers["openrouter-anthropic"].AuthScheme.ShouldBe("Bearer");
    }

    [Theory]
    [InlineData("yes", true)]
    [InlineData("1", true)]
    [InlineData("", false)]
    public void IsDefault_env_var_with_unparseable_value_is_ignored(string value, bool shouldWarn)
    {
        var (options, logger) = Resolve(
            new Dictionary<string, string?> { ["OPENCODE_GO_IS_DEFAULT"] = value },
            "opencode-go",
            new ProviderOptions { Dialect = "openai", BaseUrl = "https://o.example", IsDefault = true });

        options.Providers["opencode-go"].IsDefault.ShouldBeTrue();

        if (shouldWarn)
        {
            logger.Entries.ShouldContain(entry =>
                entry.Contains("OPENCODE_GO_IS_DEFAULT") &&
                entry.Contains("opencode-go") &&
                entry.Contains(value));
        }
        else
        {
            logger.Entries.ShouldNotContain(entry => entry.Contains("not a recognized boolean"));
        }
    }

    [Fact]
    public void IsDefault_false_overrides_bound_true()
    {
        // The false→already-true direction: a conventional _IS_DEFAULT=false must flip a provider
        // whose bound IsDefault is true (the inverse of the suffix-coverage test's true-on-false case).
        var (options, _) = Resolve(
            new Dictionary<string, string?> { ["OPENCODE_GO_IS_DEFAULT"] = "false" },
            "opencode-go",
            new ProviderOptions { Dialect = "openai", BaseUrl = "https://o.example", IsDefault = true });

        options.Providers["opencode-go"].IsDefault.ShouldBeFalse();
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
