using Microsoft.Extensions.Options;
using SmoothLlmImposter.Application.Common.Clients.Decisions;

namespace SmoothLlmImposter.Application.Features.Routing.Decisions;

/// <summary>Tool filtering as a decision-service consumer: opted in by its own enable flag (HLD 014).</summary>
internal sealed class DecisionToolsConsumer(IOptions<ImposterOptions> options) : IDecisionServiceConsumer
{
    public bool IsOptedIn => options.Value.DecisionsTools.Enabled;
}
