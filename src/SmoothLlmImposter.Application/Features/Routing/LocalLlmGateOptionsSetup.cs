using Microsoft.Extensions.Options;
using SmoothLlmImposter.Application.Common.Clients.LocalLlm;

namespace SmoothLlmImposter.Application.Features.Routing;

/// <summary>
/// Resolves the local-LLM gate budget from the post-configured <see cref="ImposterOptions"/>, so
/// <c>LOCAL_LLM_GATE_WAIT_SECONDS</c> reaches the Infrastructure gate.
/// </summary>
internal sealed class LocalLlmGateOptionsSetup(IOptions<ImposterOptions> imposter)
    : IConfigureOptions<LocalLlmGateOptions>
{
    public void Configure(LocalLlmGateOptions options) =>
        options.WaitSeconds = imposter.Value.LocalLlmGateWaitSeconds;
}
