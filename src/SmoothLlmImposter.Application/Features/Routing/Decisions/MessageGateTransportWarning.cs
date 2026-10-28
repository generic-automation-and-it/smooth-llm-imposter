using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmoothLlmImposter.Application.Features.Routing.Optimization;

namespace SmoothLlmImposter.Application.Features.Routing.Decisions;

/// <summary>
/// Before per-section profiles the message gate called the tool section's classifier. A deployment that set
/// only <c>DECISIONS_TOOLS_*</c> transport now sends the message gate to the built-in default instead — the
/// specified precedence (HLD 015 LADR-05), made visible by one startup warning. A hosted service rather than a
/// post-configure step, because the routing options are materialized more than once at startup.
/// </summary>
internal sealed class MessageGateTransportWarning(
    IOptions<ImposterOptions> imposter,
    IConfiguration configuration,
    ILogger<MessageGateTransportWarning> logger) : IHostedService
{
    private static readonly string[] EndpointAndModelFields =
        [nameof(DecisionsMessagesOptions.BaseUrl), nameof(DecisionsMessagesOptions.Path), nameof(DecisionsMessagesOptions.Model)];

    public Task StartAsync(CancellationToken cancellationToken)
    {
        ImposterOptions options = imposter.Value;
        DecisionsMessagesOptions messages = options.DecisionsMessages;
        DecisionsToolsOptions tools = options.DecisionsTools;

        if (messages.Enabled && Differs(messages, tools) && !SharedSet(options.Decisions) &&
            !SectionSet(MessagesConsumer.SectionName, ImposterOptionsPostConfigure.DecisionsMessagesVariables) &&
            SectionSet(DecisionToolsConsumer.SectionName, ImposterOptionsPostConfigure.DecisionsToolsVariables))
        {
            logger.LogWarning(
                "The message gate calls {Endpoint} with model {Model}, not the tool section's {ToolsEndpoint} with model {ToolsModel}: " +
                "DECISIONS_TOOLS_* settings apply to the tool filter only. Set DECISIONS_* (both sections) or DECISIONS_MESSAGES_* to choose the message gate's classifier.",
                SectionCircuitBreaker.Endpoint(messages.BaseUrl, messages.Path), LogSafe(messages.Model),
                SectionCircuitBreaker.Endpoint(tools.BaseUrl, tools.Path), LogSafe(tools.Model));
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static bool Differs(DecisionsMessagesOptions messages, DecisionsToolsOptions tools) =>
        !string.Equals(messages.BaseUrl.TrimEnd('/') + messages.Path, tools.BaseUrl.TrimEnd('/') + tools.Path, StringComparison.Ordinal) ||
        !string.Equals(messages.Model, tools.Model, StringComparison.Ordinal);

    private static bool SharedSet(DecisionsOptions shared) =>
        !string.IsNullOrWhiteSpace(shared.BaseUrl) ||
        !string.IsNullOrWhiteSpace(shared.Path) ||
        !string.IsNullOrWhiteSpace(shared.Model);

    private bool SectionSet(string section, IReadOnlyDictionary<string, string> variables) =>
        EndpointAndModelFields.Any(field =>
            !string.IsNullOrWhiteSpace(configuration[variables[field]]) ||
            !string.IsNullOrWhiteSpace(configuration[$"{section}:{field}"]));

    private static string LogSafe(string label) =>
        new(label.Select(static character => char.IsControl(character) ? '?' : character).ToArray());
}
