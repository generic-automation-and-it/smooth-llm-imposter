namespace SmoothLlmImposter.Application.Common.Clients.LocalLlm;

/// <summary>
/// Admission budget of the single process-wide local-LLM slot shared by every decision and optimizer call
/// (HLD 015 LADR-05). Operators set it through <c>Imposter:LocalLlmGateWaitSeconds</c> or
/// <c>LOCAL_LLM_GATE_WAIT_SECONDS</c>.
/// </summary>
public sealed class LocalLlmGateOptions
{
    public const int DefaultWaitSeconds = 60;

    public int WaitSeconds { get; set; } = DefaultWaitSeconds;
}
