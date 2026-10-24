using Microsoft.Extensions.Options;

namespace SmoothLlmImposter.Application.Common.Clients.Decisions;

/// <summary>
/// Validates the transport profile only while a consumer requires the integration; with every consumer
/// disabled, invalid unused settings stay ignored (HLD 015 LADR-02). Messages name the operator keys.
/// </summary>
internal sealed class DecisionServiceOptionsValidator(DecisionServiceDemand demand)
    : IValidateOptions<DecisionServiceOptions>
{
    private const int MinTimeoutSeconds = 1;
    private const int MaxTimeoutSeconds = 3600;
    private const string Prefix = DecisionServiceOptions.OperatorSection;

    public ValidateOptionsResult Validate(string? name, DecisionServiceOptions options)
    {
        if (!demand.IsRequired)
        {
            return ValidateOptionsResult.Success;
        }

        List<string> failures = [];

        if (string.IsNullOrWhiteSpace(options.BaseUrl) ||
            !Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out Uri? baseUrl) ||
            (baseUrl.Scheme != Uri.UriSchemeHttp && baseUrl.Scheme != Uri.UriSchemeHttps))
        {
            failures.Add($"{Prefix}:BaseUrl must be an absolute HTTP(S) URL.");
        }

        if (string.IsNullOrEmpty(options.Path) || !options.Path.StartsWith('/'))
        {
            failures.Add($"{Prefix}:Path must start with '/'.");
        }

        if (string.IsNullOrWhiteSpace(options.Model))
        {
            failures.Add($"{Prefix}:Model must be non-empty.");
        }

        if (options.MaxAttempts is < 1 or > 10)
        {
            failures.Add($"{Prefix}:MaxAttempts must be between 1 and 10.");
        }

        if (options.TimeoutSeconds is < MinTimeoutSeconds or > MaxTimeoutSeconds)
        {
            failures.Add($"{Prefix}:TimeoutSeconds must be between {MinTimeoutSeconds} and {MaxTimeoutSeconds} seconds.");
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
