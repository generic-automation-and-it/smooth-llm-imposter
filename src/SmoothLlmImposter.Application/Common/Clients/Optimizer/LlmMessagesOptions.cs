namespace SmoothLlmImposter.Application.Common.Clients.Optimizer;

/// <summary>
/// Optimizer transport settings (HLD 017). Configured only by <c>LLM_MESSAGES_*</c>; no fallback to
/// decision settings.
/// </summary>
public sealed class LlmMessagesOptions
{
    public const string SectionName = "LlmMessages";

    public bool Enabled { get; set; }

    public string BaseUrl { get; set; } = string.Empty;

    public string Path { get; set; } = "/v1/chat/completions";

    public string Model { get; set; } = string.Empty;

    public int MaxAttempts { get; set; } = 3;

    public int TimeoutSeconds { get; set; } = 60;

    public int CircuitBreakSeconds { get; set; } = 180;

    public int CacheTtlHours { get; set; } = 120;

    /// <summary>Human segments shorter than this skip both models; 0 disables the minimum.</summary>
    public int MinChars { get; set; }

    /// <summary>
    /// Time the message strategy may spend on gate and optimizer calls for one request; candidates left when it
    /// runs out are forwarded and cached as the original (HLD 017 Open item 7).
    /// </summary>
    public int BudgetSeconds { get; set; } = 30;

    public string? ApiKey { get; set; }
}