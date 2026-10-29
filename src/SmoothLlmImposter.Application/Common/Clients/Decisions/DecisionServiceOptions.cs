namespace SmoothLlmImposter.Application.Common.Clients.Decisions;

/// <summary>
/// Consumer-neutral transport profile of the shared decision service (HLD 015 LADR-02). It carries no
/// consumer's enable flag or policy. Operators still set it through the <see cref="OperatorSection"/>
/// surface and the <c>DECISIONS_TOOLS_*</c> variables, kept for compatibility.
/// </summary>
public sealed class DecisionServiceOptions
{
    public const string OperatorSection = "Imposter:DecisionsTools";
    public const string DefaultBaseUrl = "http://host.docker.internal:1234";
    public const string DefaultPath = "/v1/systemone";
    public const string DefaultModel = "openjev";
    public const int DefaultMaxAttempts = 2;
    public const int DefaultTimeoutSeconds = 60;
    public const int DefaultRetryDelaySeconds = 5;

    public string BaseUrl { get; set; } = DefaultBaseUrl;

    public string Path { get; set; } = DefaultPath;

    public string Model { get; set; } = DefaultModel;

    public int MaxAttempts { get; set; } = DefaultMaxAttempts;

    public int TimeoutSeconds { get; set; } = DefaultTimeoutSeconds;

    public int RetryDelaySeconds { get; set; } = DefaultRetryDelaySeconds;
}
