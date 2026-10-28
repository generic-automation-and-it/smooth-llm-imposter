using Microsoft.Extensions.Options;
using SmoothLlmImposter.Application.Common.Clients.Decisions;

namespace SmoothLlmImposter.Application.Features.Routing.Decisions;

/// <summary>Message optimization as a decision-service consumer (HLD 017): opted in by its own enable flag.</summary>
internal sealed class MessagesConsumer(IOptions<ImposterOptions> options) : IDecisionServiceConsumer
{
    internal const string SectionName = ImposterOptions.SectionName + ":" + nameof(ImposterOptions.DecisionsMessages);

    public string Section => SectionName;

    public bool IsOptedIn => options.Value.DecisionsMessages.Enabled;
}
