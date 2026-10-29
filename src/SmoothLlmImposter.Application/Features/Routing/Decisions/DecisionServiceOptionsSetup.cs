using Microsoft.Extensions.Options;
using SmoothLlmImposter.Application.Common.Clients.Decisions;

namespace SmoothLlmImposter.Application.Features.Routing.Decisions;

/// <summary>
/// Resolves each decision section's transport profile from its operator section, after the section → shared
/// <c>DECISIONS_*</c> → default precedence has applied (HLD 015 LADR-02, LADR-05; HLD 017 LADR-06). A section
/// never inherits another section's value, so no settings migration is needed for the tool section.
/// </summary>
internal sealed class DecisionServiceOptionsSetup(IOptions<ImposterOptions> imposter)
    : IConfigureNamedOptions<DecisionServiceOptions>
{
    public void Configure(string? name, DecisionServiceOptions options)
    {
        switch (name)
        {
            case DecisionToolsConsumer.SectionName:
                DecisionsToolsOptions tools = imposter.Value.DecisionsTools;
                Apply(options, tools.BaseUrl, tools.Path, tools.Model, tools.MaxAttempts, tools.TimeoutSeconds, tools.RetryDelaySeconds);
                break;
            case MessagesConsumer.SectionName:
                DecisionsMessagesOptions messages = imposter.Value.DecisionsMessages;
                Apply(options, messages.BaseUrl, messages.Path, messages.Model, messages.MaxAttempts, messages.TimeoutSeconds, messages.RetryDelaySeconds);
                break;
        }
    }

    public void Configure(DecisionServiceOptions options) => Configure(Options.DefaultName, options);

    private static void Apply(
        DecisionServiceOptions options, string baseUrl, string path, string model, int maxAttempts, int timeoutSeconds,
        int retryDelaySeconds)
    {
        options.BaseUrl = baseUrl;
        options.Path = path;
        options.Model = model;
        options.MaxAttempts = maxAttempts;
        options.TimeoutSeconds = timeoutSeconds;
        options.RetryDelaySeconds = retryDelaySeconds;
    }
}
