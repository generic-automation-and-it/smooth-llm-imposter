namespace SmoothLlmImposter.Application.Common.Clients.Decisions;

/// <summary>
/// Consumer-neutral transport profile of the shared decision service (HLD 015 LADR-02). One named instance
/// exists per decision section, named by <see cref="IDecisionServiceConsumer.Section"/> (HLD 015 LADR-05). It
/// carries no consumer's enable flag or policy; each section resolves its values from its own operator
/// section, then the shared <c>Imposter:Decisions</c>/<c>DECISIONS_*</c> layer, then these defaults
/// (HLD 017 LADR-06).
/// </summary>
public sealed class DecisionServiceOptions
{
    public const string DefaultBaseUrl = "http://host.docker.internal:1234";
    public const string DefaultPath = "/v1/systemone";
    public const string DefaultModel = "openjev";
    public const int DefaultMaxAttempts = 3;
    public const int DefaultTimeoutSeconds = 60;
    public const int MaxAttemptsLimit = 10;

    public string BaseUrl { get; set; } = DefaultBaseUrl;

    public string Path { get; set; } = DefaultPath;

    public string Model { get; set; } = DefaultModel;

    public int MaxAttempts { get; set; } = DefaultMaxAttempts;

    public int TimeoutSeconds { get; set; } = DefaultTimeoutSeconds;

    /// <summary>The full request URL; with <see cref="Model"/> it keys the admission gate.</summary>
    public string Endpoint => BaseUrl.TrimEnd('/') + Path;
}
