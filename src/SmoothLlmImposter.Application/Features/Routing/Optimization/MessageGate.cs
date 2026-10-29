using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using SmoothLlmImposter.Application.Common.Clients.Decisions;
using SmoothLlmImposter.Application.Features.Routing;
using SmoothLlmImposter.Application.Features.Routing.Decisions;

namespace SmoothLlmImposter.Application.Features.Routing.Optimization;

internal sealed class MessageGate(
    IDecisionModelClient client,
    IOptions<ImposterOptions> options,
    DecisionServiceProfiles profiles) : IMessageGate
{
    private const string QuestionKey = "optimize";

    public string Model => Profile.Model;

    public string Endpoint => SectionCircuitBreaker.Endpoint(Profile.BaseUrl, Profile.Path);

    private DecisionServiceOptions Profile => profiles.Resolve(MessagesConsumer.SectionName);

    public async Task<bool> CanOptimizeAsync(string humanText, CancellationToken cancellationToken)
    {
        JsonObject envelope = await client.DecideAsync(MessagesConsumer.SectionName, new JsonObject
        {
            ["state"] = humanText,
            ["questions"] = new JsonObject { [QuestionKey] = Question() }
        }, cancellationToken);

        if (envelope["answers"] is not JsonObject answers ||
            answers[QuestionKey] is not JsonObject answer ||
            answer["probabilities"] is not JsonObject scores ||
            scores[QuestionKey] is not JsonValue value ||
            !value.TryGetValue(out double probability) ||
            !double.IsFinite(probability) ||
            probability is < 0 or > 1)
        {
            throw new InvalidOperationException("The message gate returned an invalid answer.");
        }

        return probability >= options.Value.DecisionsMessages.MinProbability;
    }

    private static JsonObject Question() => new()
    {
        ["type"] = "choice",
        ["instructions"] = "Should this user message be tightened? Keep it unchanged if any detail, command, path, code, negation or intent could change.",
        ["criteria"] = new JsonObject
        {
            [QuestionKey] = "the message can be shorter without losing meaning",
            ["keep"] = "the message should stay as written"
        }
    };
}
