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
