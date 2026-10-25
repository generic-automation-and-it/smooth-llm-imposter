using Microsoft.Extensions.Options;
using SmoothLlmImposter.Application.Common.Clients.Decisions;

namespace SmoothLlmImposter.Application.Features.Routing.Decisions;

/// <summary>
/// Resolves the shared transport profile from the existing tool-filter operator surface, after its
/// conventional-over-structured precedence has applied, so no settings migration is needed (HLD 015 LADR-02).
/// </summary>
internal sealed class DecisionServiceOptionsSetup(IOptions<ImposterOptions> imposter)
    : IConfigureOptions<DecisionServiceOptions>
{
    public void Configure(DecisionServiceOptions options)
    {
        DecisionsToolsOptions source = imposter.Value.DecisionsTools;
        options.BaseUrl = source.BaseUrl;
        options.Path = source.Path;
        options.Model = source.Model;
        options.MaxAttempts = source.MaxAttempts;
        options.TimeoutSeconds = source.TimeoutSeconds;
    }
}
