using Microsoft.Extensions.Options;
using SmoothLlmImposter.Application.Common.Clients.Decisions;

namespace SmoothLlmImposter.Application.UnitTest.Common.Clients.Decisions;

public class DecisionServiceOptionsValidatorTests
{
    private const string Section = "Imposter:DecisionsTools";

    private static ValidateOptionsResult Validate(DecisionServiceOptions options, params bool[] consumersOptedIn) =>
        new DecisionServiceOptionsValidator(
            consumersOptedIn.Select(static optedIn => new FixtureConsumer(optedIn, Section)).ToArray())
            .Validate(Section, options);

    private static DecisionServiceOptions Invalid() => new()
    {
        BaseUrl = "not-a-url",
        Path = "relative",
        Model = " ",
        MaxAttempts = 0,
        TimeoutSeconds = 0
    };

    [Fact]
    public void Without_consumers_invalid_settings_are_ignored() =>
        Validate(Invalid()).Succeeded.ShouldBeTrue();

    [Fact]
    public void With_every_consumer_opted_out_invalid_settings_are_ignored() =>
        Validate(Invalid(), false, false).Succeeded.ShouldBeTrue();

    [Fact]
    public void Another_sections_opted_in_consumer_leaves_this_sections_invalid_settings_ignored()
    {
        // HLD 015 LADR-05: validation follows each section's own enablement.
        var validator = new DecisionServiceOptionsValidator(
            [new FixtureConsumer(false, Section), new FixtureConsumer(true, "Imposter:DecisionsMessages")]);

        validator.Validate(Section, Invalid()).Succeeded.ShouldBeTrue();
        validator.Validate("Imposter:DecisionsMessages", Invalid()).Failed.ShouldBeTrue();
        validator.Validate(null, Invalid()).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void The_sections_opted_in_consumer_requires_valid_settings()
    {
        ValidateOptionsResult result = Validate(Invalid(), false, true);

        result.Failed.ShouldBeTrue();
        foreach (string field in new[] { "BaseUrl", "Path", "Model", "MaxAttempts", "TimeoutSeconds" })
        {
            result.FailureMessage.ShouldContain($"Imposter:DecisionsTools:{field}");
        }
    }

    [Fact]
    public void Default_settings_are_valid_for_an_opted_in_consumer() =>
        Validate(new DecisionServiceOptions(), true).Succeeded.ShouldBeTrue();

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
    public void Invalid_strings_fail(string field, string value)
    {
        var options = new DecisionServiceOptions();
        switch (field)
        {
            case "BaseUrl": options.BaseUrl = value; break;
            case "Path": options.Path = value; break;
            case "Model": options.Model = value; break;
        }

        ValidateOptionsResult result = Validate(options, true);

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain($"Imposter:DecisionsTools:{field}");
    }

    [Theory]
    [InlineData("http://127.0.0.1:1234")]
    [InlineData("https://classifier.example")]
    [InlineData("HTTPS://classifier.example")]
    public void Http_and_https_destinations_are_accepted(string baseUrl) =>
        Validate(new DecisionServiceOptions { BaseUrl = baseUrl }, true).Succeeded.ShouldBeTrue();

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 60)]
    [InlineData(10, 3600)]
    public void Attempt_and_timeout_boundaries_are_accepted(int attempts, int timeout) =>
        Validate(new DecisionServiceOptions { MaxAttempts = attempts, TimeoutSeconds = timeout }, true)
            .Succeeded.ShouldBeTrue();

    [Theory]
    [InlineData(0, 60, "MaxAttempts")]
    [InlineData(-1, 60, "MaxAttempts")]
    [InlineData(11, 60, "MaxAttempts")]
    [InlineData(3, 0, "TimeoutSeconds")]
    [InlineData(3, -1, "TimeoutSeconds")]
    [InlineData(3, 3601, "TimeoutSeconds")]
    public void Invalid_attempts_or_timeout_fail(int attempts, int timeout, string field)
    {
        ValidateOptionsResult result =
            Validate(new DecisionServiceOptions { MaxAttempts = attempts, TimeoutSeconds = timeout }, true);

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain($"Imposter:DecisionsTools:{field}");
    }

    private sealed class FixtureConsumer(bool optedIn, string section) : IDecisionServiceConsumer
    {
        public string Section => section;

        public bool IsOptedIn => optedIn;
    }
}
