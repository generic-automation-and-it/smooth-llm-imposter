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
    [InlineData(1)]
    [InlineData(180)]
    [InlineData(3600)]
    public void Decision_circuit_break_within_range_is_accepted(int seconds)
    {
        var options = Options(Valid("a"));
        options.DecisionsTools.Enabled = true;
        options.DecisionsTools.CircuitBreakSeconds = seconds;

        _validator.Validate(null, options).Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3601)]
    public void Decision_circuit_break_out_of_range_fails(int seconds)
    {
        var options = Options(Valid("a"));
        options.DecisionsTools.Enabled = true;
        options.DecisionsTools.CircuitBreakSeconds = seconds;

        ValidateOptionsResult result = _validator.Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("Imposter:DecisionsTools:CircuitBreakSeconds");
    }

    [Fact]
    public void Disabled_decisions_tools_ignore_an_invalid_circuit_break()
    {
        var options = Options(Valid("a"));
        options.DecisionsTools.Enabled = false;
        options.DecisionsTools.CircuitBreakSeconds = 0;

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

    [Fact]
    public void Disabled_optimizer_ignores_invalid_settings()
    {
        var options = Options(Valid("a"));
        options.LlmMessages.Enabled = false;
        options.LlmMessages.BaseUrl = "not-a-url";
        options.LlmMessages.Model = "";
        options.LlmMessages.CacheTtlHours = 0;
        options.LlmMessages.MaxAttempts = 0;
        options.LlmMessages.TimeoutSeconds = 0;
        options.LlmMessages.CircuitBreakSeconds = 0;

        _validator.Validate(null, options).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Enabled_optimizer_accepts_complete_settings()
    {
        var options = Options(Valid("a"));
        options.LlmMessages.Enabled = true;
        options.LlmMessages.BaseUrl = "https://opt.example";
        options.LlmMessages.Model = "opt-model";

        _validator.Validate(null, options).Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("ftp://opt.example")]
    [InlineData("relative/path")]
    public void Enabled_optimizer_requires_an_absolute_http_url(string baseUrl)
    {
        var options = Options(Valid("a"));
        options.LlmMessages.Enabled = true;
        options.LlmMessages.Model = "opt-model";
        options.LlmMessages.BaseUrl = baseUrl;

        _validator.Validate(null, options).Failed.ShouldBeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(121)]
    public void Enabled_optimizer_rejects_cache_ttl_outside_one_to_120_hours(int hours)
    {
        var options = Options(Valid("a"));
        options.LlmMessages.Enabled = true;
        options.LlmMessages.BaseUrl = "https://opt.example";
        options.LlmMessages.Model = "opt-model";
        options.LlmMessages.CacheTtlHours = hours;

        _validator.Validate(null, options).Failed.ShouldBeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3601)]
    public void Enabled_message_gate_rejects_circuit_break_outside_range(int seconds)
    {
        var options = Options(Valid("a"));
        options.DecisionsMessages.Enabled = true;
        options.DecisionsMessages.CircuitBreakSeconds = seconds;

        _validator.Validate(null, options).Failed.ShouldBeTrue();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3600)]
    public void Enabled_message_gate_accepts_circuit_break_at_the_boundaries(int seconds)
    {
        var options = Options(Valid("a"));
        options.DecisionsMessages.Enabled = true;
        options.DecisionsMessages.CircuitBreakSeconds = seconds;

        _validator.Validate(null, options).Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(10_001)]
    public void Enabled_optimizer_rejects_min_chars_outside_range(int minChars)
    {
        var options = Options(Valid("a"));
        options.LlmMessages.Enabled = true;
        options.LlmMessages.BaseUrl = "https://opt.example";
        options.LlmMessages.Model = "opt-model";
        options.LlmMessages.MinChars = minChars;

        _validator.Validate(null, options).Failed.ShouldBeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10_000)]
    public void Enabled_optimizer_accepts_min_chars_at_the_boundaries(int minChars)
    {
        var options = Options(Valid("a"));
        options.LlmMessages.Enabled = true;
        options.LlmMessages.BaseUrl = "https://opt.example";
        options.LlmMessages.Model = "opt-model";
        options.LlmMessages.MinChars = minChars;

        _validator.Validate(null, options).Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(601)]
    public void Enabled_optimizer_rejects_budget_outside_one_to_600_seconds(int seconds)
    {
        var options = Options(Valid("a"));
        options.LlmMessages.Enabled = true;
        options.LlmMessages.BaseUrl = "https://opt.example";
        options.LlmMessages.Model = "opt-model";
        options.LlmMessages.BudgetSeconds = seconds;

        var result = _validator.Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("Imposter:LlmMessages:BudgetSeconds must be between 1 and 600 seconds.");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(600)]
    public void Enabled_optimizer_accepts_budget_at_the_boundaries(int seconds)
    {
        var options = Options(Valid("a"));
        options.LlmMessages.Enabled = true;
        options.LlmMessages.BaseUrl = "https://opt.example";
        options.LlmMessages.Model = "opt-model";
        options.LlmMessages.BudgetSeconds = seconds;

        _validator.Validate(null, options).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Disabled_optimizer_ignores_an_invalid_budget()
    {
        var options = Options(Valid("a"));
        options.LlmMessages.BudgetSeconds = 0;

        _validator.Validate(null, options).Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public void Enabled_optimizer_rejects_max_attempts_outside_range(int attempts)
    {
        var options = Options(Valid("a"));
        options.LlmMessages.Enabled = true;
        options.LlmMessages.BaseUrl = "https://opt.example";
        options.LlmMessages.Model = "opt-model";
        options.LlmMessages.MaxAttempts = attempts;

        _validator.Validate(null, options).Failed.ShouldBeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3601)]
    public void Enabled_optimizer_rejects_timeout_outside_range(int seconds)
    {
        var options = Options(Valid("a"));
        options.LlmMessages.Enabled = true;
        options.LlmMessages.BaseUrl = "https://opt.example";
        options.LlmMessages.Model = "opt-model";
        options.LlmMessages.TimeoutSeconds = seconds;

        _validator.Validate(null, options).Failed.ShouldBeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3601)]
    public void Enabled_optimizer_rejects_circuit_break_outside_range(int seconds)
    {
        var options = Options(Valid("a"));
        options.LlmMessages.Enabled = true;
        options.LlmMessages.BaseUrl = "https://opt.example";
        options.LlmMessages.Model = "opt-model";
        options.LlmMessages.CircuitBreakSeconds = seconds;

        _validator.Validate(null, options).Failed.ShouldBeTrue();
    }

    [Fact]
    public void Enabled_optimizer_requires_a_model()
    {
        var options = Options(Valid("a"));
        options.LlmMessages.Enabled = true;
        options.LlmMessages.BaseUrl = "https://opt.example";
        options.LlmMessages.Model = " ";

        _validator.Validate(null, options).Failed.ShouldBeTrue();
    }
}
