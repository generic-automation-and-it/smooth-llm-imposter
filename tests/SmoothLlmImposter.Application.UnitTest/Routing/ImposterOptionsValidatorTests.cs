using Microsoft.Extensions.Options;
using SmoothLlmImposter.Application.Features.Routing;

namespace SmoothLlmImposter.Application.UnitTest.Routing;

public class ImposterOptionsValidatorTests
{
    private readonly ImposterOptionsValidator _validator = new();

    private static ImposterOptions Options(params ProviderOptions[] providers) =>
        new() { Providers = [.. providers] };

    private static ProviderOptions Valid(string name, string dialect = "openai", bool isDefault = false) =>
        new() { Name = name, Dialect = dialect, BaseUrl = "https://" + name + ".example", IsDefault = isDefault };

    [Fact]
    public void Valid_configuration_succeeds() =>
        _validator.Validate(null, Options(Valid("a", isDefault: true))).Succeeded.ShouldBeTrue();

    [Fact]
    public void Empty_providers_fails() =>
        _validator.Validate(null, Options()).Failed.ShouldBeTrue();

    [Fact]
    public void Duplicate_names_fail() =>
        _validator.Validate(null, Options(Valid("dup"), Valid("dup"))).Failed.ShouldBeTrue();

    [Fact]
    public void Unknown_dialect_fails() =>
        _validator.Validate(null, Options(Valid("a", dialect: "gemini"))).Failed.ShouldBeTrue();

    [Fact]
    public void Non_absolute_base_url_fails()
    {
        var bad = new ProviderOptions { Name = "a", Dialect = "openai", BaseUrl = "not-a-url" };
        _validator.Validate(null, Options(bad)).Failed.ShouldBeTrue();
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
            Name = "a", Dialect = "openai", BaseUrl = "https://a.example",
            Models = [new ModelMappingOptions { From = "x", To = "" }]
        };
        _validator.Validate(null, Options(provider)).Failed.ShouldBeTrue();
    }

    [Fact]
    public void Invalid_auth_scheme_fails()
    {
        var provider = new ProviderOptions
        {
            Name = "a", Dialect = "openai", BaseUrl = "https://a.example", AuthScheme = "Token"
        };
        _validator.Validate(null, Options(provider)).Failed.ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ApiKey")]
    [InlineData("bearer")]
    public void Known_or_omitted_auth_scheme_succeeds(string? authScheme)
    {
        var provider = new ProviderOptions
        {
            Name = "a", Dialect = "openai", BaseUrl = "https://a.example", AuthScheme = authScheme
        };
        _validator.Validate(null, Options(provider)).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Invalid_request_normalization_fails()
    {
        var provider = new ProviderOptions
        {
            Name = "a", Dialect = "openai", BaseUrl = "https://a.example", RequestNormalization = "rename"
        };
        _validator.Validate(null, Options(provider)).Failed.ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("none")]
    [InlineData("codex_to_openai_sdk")]
    public void Known_or_omitted_request_normalization_succeeds(string? normalization)
    {
        var provider = new ProviderOptions
        {
            Name = "a", Dialect = "openai", BaseUrl = "https://a.example", RequestNormalization = normalization
        };
        _validator.Validate(null, Options(provider)).Succeeded.ShouldBeTrue();
    }
}
