using Microsoft.Extensions.Options;

namespace SmoothLlmImposter.Application.Common.Clients.Decisions;

/// <summary>
/// Validates a section's transport profile only while that section's consumer is opted in; a disabled
/// section's invalid, unused settings stay ignored (HLD 015 LADR-02, LADR-05). Messages name the operator keys.
/// </summary>
internal sealed class DecisionServiceOptionsValidator(IEnumerable<IDecisionServiceConsumer> consumers)
    : IValidateOptions<DecisionServiceOptions>
{
    private const int MinTimeoutSeconds = 1;
    private const int MaxTimeoutSeconds = 3600;

    public ValidateOptionsResult Validate(string? name, DecisionServiceOptions options)
    {
        if (name is null || !consumers.Any(consumer =>
                consumer.IsOptedIn && string.Equals(consumer.Section, name, StringComparison.Ordinal)))
        {
            return ValidateOptionsResult.Success;
        }

        List<string> failures = TransportFailures(name, options);

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }

    /// <summary>
    /// The transport rules every decision section shares, reported under the operator section that set them.
    /// </summary>
    internal static List<string> TransportFailures(string prefix, DecisionServiceOptions options)
    {
        List<string> failures = [];

        if (string.IsNullOrWhiteSpace(options.BaseUrl) ||
            !Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out Uri? baseUrl) ||
            (baseUrl.Scheme != Uri.UriSchemeHttp && baseUrl.Scheme != Uri.UriSchemeHttps))
        {
            failures.Add($"{prefix}:BaseUrl must be an absolute HTTP(S) URL.");
        }

        if (string.IsNullOrEmpty(options.Path) || !options.Path.StartsWith('/'))
        {
            failures.Add($"{prefix}:Path must start with '/'.");
        }

        if (string.IsNullOrWhiteSpace(options.Model))
        {
            failures.Add($"{prefix}:Model must be non-empty.");
        }

        if (options.MaxAttempts is < 1 or > DecisionServiceOptions.MaxAttemptsLimit)
        {
            failures.Add($"{prefix}:MaxAttempts must be between 1 and {DecisionServiceOptions.MaxAttemptsLimit}.");
        }

        if (options.TimeoutSeconds is < MinTimeoutSeconds or > MaxTimeoutSeconds)
        {
            failures.Add($"{prefix}:TimeoutSeconds must be between {MinTimeoutSeconds} and {MaxTimeoutSeconds} seconds.");
        }

        return failures;
    }
}
