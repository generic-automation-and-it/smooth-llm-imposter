using Microsoft.Extensions.Options;
using SmoothLlmImposter.Application.Common.Clients.Optimizer;
using SmoothLlmImposter.Application.Features.Routing;

namespace SmoothLlmImposter.Application.Features.Routing.Optimization;

/// <summary>
/// Resolves the optimizer transport settings from the post-configured <see cref="ImposterOptions.LlmMessages"/>
/// instance, so <c>LLM_MESSAGES_*</c> reaches the client, cache and circuit (HLD 017 LADR-06). Without this
/// setup <see cref="IOptions{LlmMessagesOptions}"/> falls back to an unbound instance.
/// </summary>
internal sealed class LlmMessagesOptionsSetup(IOptions<ImposterOptions> imposter)
    : IConfigureOptions<LlmMessagesOptions>
{
    public void Configure(LlmMessagesOptions options)
    {
        LlmMessagesOptions source = imposter.Value.LlmMessages;
        options.Enabled = source.Enabled;
        options.BaseUrl = source.BaseUrl;
        options.Path = source.Path;
        options.Model = source.Model;
        options.MaxAttempts = source.MaxAttempts;
        options.TimeoutSeconds = source.TimeoutSeconds;
        options.CircuitBreakSeconds = source.CircuitBreakSeconds;
        options.CacheTtlHours = source.CacheTtlHours;
        options.MinChars = source.MinChars;
        options.BudgetSeconds = source.BudgetSeconds;
        options.ApiKey = source.ApiKey;
    }
}
