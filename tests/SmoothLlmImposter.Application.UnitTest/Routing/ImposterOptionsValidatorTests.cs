using Microsoft.Extensions.Options;
using SmoothLlmImposter.Application.Features.Routing;

namespace SmoothLlmImposter.Application.UnitTest.Routing;

public class ImposterOptionsValidatorTests
{
    private readonly ImposterOptionsValidator _validator = new();

    private static ImposterOptions Options(params (string Key, ProviderOptions Provider)[] entries) =>
        new() { Providers = entries.ToDictionary(static e => e.Key, static e => e.Provider, StringComparer.Ordinal) };

    private static (string, ProviderOptions) Valid(string key, string dialect = "openai", bool isDefault = false) =>
        (key, new ProviderOptions { Dialect = dialect, BaseUrl = "https://" + key + ".example", IsDefault = isDefault });

    [Fact]
    public void Valid_configuration_succeeds() =>
        _validator.Validate(null, Options(Valid("a", isDefault: true))).Succeeded.ShouldBeTrue();

    [Fact]
    public void Disabled_decision_filter_ignores_invalid_decision_settings()
    {
        var options = Options(Valid("a"));
        options.DecisionsTools.BaseUrl = "not-a-url";
        options.DecisionsTools.Path = "relative";
        options.DecisionsTools.Model = " ";
        options.DecisionsTools.MinProbability = double.NaN;
        options.DecisionsTools.MaxAttempts = 0;
        options.DecisionsTools.TimeoutSeconds = 0;

        _validator.Validate(null, options).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Enabled_decision_filter_accepts_default_settings()
    {
        var options = Options(Valid("a"));
        options.DecisionsTools.Enabled = true;

        _validator.Validate(null, options).Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData("BaseUrl", "not-a-url")]
    [InlineData("BaseUrl", "")]
    [InlineData("BaseUrl", "file:///tmp/decisions")]
    [InlineData("BaseUrl", "ftp://classifier.example")]
    [InlineData("BaseUrl", "mailto:classifier@example.com")]
    [InlineData("Path", "relative")]
    [InlineData("Path", "")]
    [InlineData("Model", "")]
    [InlineData("Model", "   ")]
    public void Enabled_decision_filter_rejects_invalid_strings(string field, string value)
    {
        var options = Options(Valid("a"));
        options.DecisionsTools.Enabled = true;
        switch (field)
        {
            case "BaseUrl": options.DecisionsTools.BaseUrl = value; break;
            case "Path": options.DecisionsTools.Path = value; break;
            case "Model": options.DecisionsTools.Model = value; break;
        }

        ValidateOptionsResult result = _validator.Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain($"Imposter:DecisionsTools:{field}");
    }

    [Theory]
    [InlineData("http://127.0.0.1:1234")]
    [InlineData("https://classifier.example")]
    [InlineData("HTTPS://classifier.example")]
    public void Enabled_decision_filter_accepts_http_and_https(string baseUrl)
    {
        var options = Options(Valid("a"));
        options.DecisionsTools.Enabled = true;
        options.DecisionsTools.BaseUrl = baseUrl;

        _validator.Validate(null, options).Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData("file:///tmp/decisions")]
    [InlineData("ftp://classifier.example")]
    public void Disabled_decision_filter_ignores_unsupported_url_schemes(string baseUrl)
    {
        var options = Options(Valid("a"));
        options.DecisionsTools.BaseUrl = baseUrl;

        _validator.Validate(null, options).Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0.5)]
    [InlineData(1)]
    public void Decision_probability_including_boundaries_is_accepted(double probability)
    {
        var options = Options(Valid("a"));
        options.DecisionsTools.Enabled = true;
        options.DecisionsTools.MinProbability = probability;

        _validator.Validate(null, options).Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(20)]
    [InlineData(1000)]
    public void Decision_minimum_kept_tools_within_range_is_accepted(int minimum)
    {
        var options = Options(Valid("a"));
        options.DecisionsTools.Enabled = true;
        options.DecisionsTools.MinKeepTools = minimum;

        _validator.Validate(null, options).Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1001)]
    public void Decision_minimum_kept_tools_out_of_range_fails(int minimum)
    {
        var options = Options(Valid("a"));
        options.DecisionsTools.Enabled = true;
        options.DecisionsTools.MinKeepTools = minimum;

        ValidateOptionsResult result = _validator.Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("Imposter:DecisionsTools:MinKeepTools");
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Invalid_decision_probability_fails(double probability)
    {
        var options = Options(Valid("a"));
        options.DecisionsTools.Enabled = true;
        options.DecisionsTools.MinProbability = probability;

        ValidateOptionsResult result = _validator.Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("Imposter:DecisionsTools:MinProbability");
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 60)]
    [InlineData(10, 3600)]
    public void Decision_attempt_and_timeout_boundaries_are_accepted(int attempts, int timeout)
    {
        var options = Options(Valid("a"));
        options.DecisionsTools.Enabled = true;
        options.DecisionsTools.MaxAttempts = attempts;
        options.DecisionsTools.TimeoutSeconds = timeout;

        _validator.Validate(null, options).Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData(0, 60, "MaxAttempts")]
    [InlineData(-1, 60, "MaxAttempts")]
    [InlineData(11, 60, "MaxAttempts")]
    [InlineData(3, 0, "TimeoutSeconds")]
    [InlineData(3, -1, "TimeoutSeconds")]
    [InlineData(3, 3601, "TimeoutSeconds")]
    public void Invalid_decision_attempts_or_timeout_fail(int attempts, int timeout, string field)
    {
        var options = Options(Valid("a"));
        options.DecisionsTools.Enabled = true;
        options.DecisionsTools.MaxAttempts = attempts;
        options.DecisionsTools.TimeoutSeconds = timeout;

        ValidateOptionsResult result = _validator.Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain($"Imposter:DecisionsTools:{field}");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(300)]
    [InlineData(3600)]
    public void A_timeout_inside_the_supported_range_is_accepted(int seconds)
    {
        var provider = new ProviderOptions { Dialect = "openai", BaseUrl = "https://a.example", TimeoutSeconds = seconds };

        _validator.Validate(null, Options(("a", provider))).Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(3601)]
    public void A_timeout_outside_the_supported_range_fails_fast(int seconds)
    {
        // Fail at startup rather than silently falling back to the default: an operator who typed 0 would
        // otherwise never learn their override was discarded (HLD 012).
        var provider = new ProviderOptions { Dialect = "openai", BaseUrl = "https://a.example", TimeoutSeconds = seconds };

        ValidateOptionsResult result = _validator.Validate(null, Options(("a", provider)));

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("TimeoutSeconds");
    }

    [Fact]
    public void Disabled_default_can_coexist_with_an_enabled_default_for_the_same_dialect()
    {
        // "At most one default per dialect" counts only ENABLED defaults (HLD 008): a disabled default must
        // not collide with the live one. Re-enabling it into a duplicate is gated by the admin mutation validator.
        var live = ("live", new ProviderOptions { Dialect = "openai", BaseUrl = "https://live.example", IsDefault = true });
        var shadow = ("shadow", new ProviderOptions { Dialect = "openai", BaseUrl = "https://shadow.example", IsDefault = true, Enabled = false });

        _validator.Validate(null, Options(live, shadow)).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Empty_providers_fails() =>
        _validator.Validate(null, Options()).Failed.ShouldBeTrue();

    [Fact]
    public void Numeric_keys_fail_with_migration_message()
    {
        // A legacy JSON array binds into the dictionary as keys "0","1",… — the un-migrated array shape.
        var result = _validator.Validate(null, Options(Valid("0"), Valid("1")));

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldContain(f => f.Contains("name-keyed object") && f.Contains("\"<name>\""));
    }

    [Fact]
    public void Case_only_duplicate_keys_fail()
    {
        // Ordinal dict keeps these distinct; the validator must reject them as a duplicate (NFR-02).
        var result = _validator.Validate(null, Options(Valid("opencode-go"), Valid("OpenCode-Go")));

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldContain(f => f.Contains("duplicated"));
    }

    [Fact]
    public void Duplicate_explicit_names_across_keys_fail()
    {
        var a = (Key: "a", Provider: new ProviderOptions { Name = "shared", Dialect = "openai", BaseUrl = "https://a.example" });
        var b = (Key: "b", Provider: new ProviderOptions { Name = "shared", Dialect = "openai", BaseUrl = "https://b.example" });

        _validator.Validate(null, Options(a, b)).Failed.ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_name_override_fails(string blank)
    {
        var provider = new ProviderOptions { Name = blank, Dialect = "openai", BaseUrl = "https://a.example" };

        var result = _validator.Validate(null, Options(("a", provider)));

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldContain(f => f.Contains("Name override is blank"));
    }

    [Fact]
    public void Omitted_name_uses_key_and_succeeds()
    {
        // Name null = "use the key"; this is the common case and must pass.
        var provider = new ProviderOptions { Dialect = "openai", BaseUrl = "https://a.example" };
        _validator.Validate(null, Options(("a", provider))).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Set_name_override_succeeds()
    {
        var provider = new ProviderOptions { Name = "display", Dialect = "openai", BaseUrl = "https://a.example" };
        _validator.Validate(null, Options(("a", provider))).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Unknown_dialect_fails() =>
        _validator.Validate(null, Options(Valid("a", dialect: "gemini"))).Failed.ShouldBeTrue();

    [Fact]
    public void Non_absolute_base_url_fails()
    {
        var bad = new ProviderOptions { Dialect = "openai", BaseUrl = "not-a-url" };
        _validator.Validate(null, Options(("a", bad))).Failed.ShouldBeTrue();
    }

    [Fact]
    public void Two_defaults_for_same_dialect_fail() =>
        _validator.Validate(null, Options(Valid("a", isDefault: true), Valid("b", isDefault: true)))
            .Failed.ShouldBeTrue();

    [Fact]
    public void Mapping_missing_to_fails()
    {
        var provider = new ProviderOptions
        {
            Dialect = "openai", BaseUrl = "https://a.example",
            Models = [new ModelMappingOptions { From = "x", To = "" }]
        };
        _validator.Validate(null, Options(("a", provider))).Failed.ShouldBeTrue();
    }

    [Fact]
    public void Invalid_auth_scheme_fails()
    {
        var provider = new ProviderOptions { Dialect = "openai", BaseUrl = "https://a.example", AuthScheme = "Token" };
        _validator.Validate(null, Options(("a", provider))).Failed.ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ApiKey")]
    [InlineData("bearer")]
    public void Known_or_omitted_auth_scheme_succeeds(string? authScheme)
    {
        var provider = new ProviderOptions { Dialect = "openai", BaseUrl = "https://a.example", AuthScheme = authScheme };
        _validator.Validate(null, Options(("a", provider))).Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("api-key")]
    [InlineData("x-custom_auth")]
    public void Known_or_omitted_auth_header_succeeds(string? authHeader)
    {
        var provider = new ProviderOptions { Dialect = "openai", BaseUrl = "https://a.example", AuthHeader = authHeader };
        _validator.Validate(null, Options(("a", provider))).Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Content-Type")]
    [InlineData("content-length")]
    [InlineData("Host")]
    [InlineData("Transfer-Encoding")]
    [InlineData("api key")]
    [InlineData("api:key")]
    [InlineData("api\r\nkey")]
    public void Invalid_auth_header_fails(string authHeader)
    {
        var provider = new ProviderOptions { Dialect = "openai", BaseUrl = "https://a.example", AuthHeader = authHeader };

        var result = _validator.Validate(null, Options(("a", provider)));

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldContain(f => f.Contains("AuthHeader must be omitted or a custom request-header name"));
    }

    [Fact]
    public void Invalid_request_normalization_fails()
    {
        var provider = new ProviderOptions { Dialect = "openai", BaseUrl = "https://a.example", RequestNormalization = "rename" };
        _validator.Validate(null, Options(("a", provider))).Failed.ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("none")]
    public void Known_or_omitted_request_normalization_succeeds(string? normalization)
    {
        var provider = new ProviderOptions { Dialect = "openai", BaseUrl = "https://a.example", RequestNormalization = normalization };
        _validator.Validate(null, Options(("a", provider))).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Explicit_codex_normalization_on_chat_completions_succeeds()
    {
        var provider = new ProviderOptions
        {
            Dialect = "openai", BaseUrl = "https://a.example",
            OpenAiUpstreamApi = "chat_completions", RequestNormalization = "codex_to_openai_sdk"
        };
        _validator.Validate(null, Options(("a", provider))).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Explicit_codex_normalization_on_responses_upstream_fails()
    {
        // responses default: codex_to_openai_sdk would strip valid Responses tool types.
        var provider = new ProviderOptions
        {
            Dialect = "openai", BaseUrl = "https://a.example", RequestNormalization = "codex_to_openai_sdk"
        };
        _validator.Validate(null, Options(("a", provider))).Failed.ShouldBeTrue();
    }

    [Fact]
    public void Explicit_codex_normalization_on_anthropic_dialect_fails()
    {
        var provider = new ProviderOptions
        {
            Dialect = "anthropic", BaseUrl = "https://a.example",
            OpenAiUpstreamApi = "chat_completions", RequestNormalization = "codex_to_openai_sdk"
        };
        _validator.Validate(null, Options(("a", provider))).Failed.ShouldBeTrue();
    }

    [Fact]
    public void Invalid_session_forwarding_fails()
    {
        var provider = new ProviderOptions { Dialect = "openai", BaseUrl = "https://a.example", SessionForwarding = "sticky" };
        _validator.Validate(null, Options(("a", provider))).Failed.ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("none")]
    [InlineData("opencode-go")]
    [InlineData("opencode_go")]
    [InlineData("opencodego")]
    [InlineData("OpenCode-Go")]
    [InlineData("OPENCODE-GO")]
    public void Known_or_omitted_session_forwarding_succeeds(string? forwarding)
    {
        var provider = new ProviderOptions { Dialect = "openai", BaseUrl = "https://a.example", SessionForwarding = forwarding };
        _validator.Validate(null, Options(("a", provider))).Succeeded.ShouldBeTrue();
    }
}
