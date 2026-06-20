using System.Text.Json.Nodes;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.Features.Routing.Normalization;

/// <summary>
/// A proxy-side request-normalization stage (HLD 004): reshapes the inbound request body so a strict
/// OpenAI-compatible upstream accepts it. Implementations are <b>request-only</b> — they mutate the
/// parsed request node graph in place and never read or rewrite the response (LADR-02) — and must be
/// <b>idempotent</b> (re-running on already-normalized input is a no-op, NFR-02).
/// </summary>
/// <remarks>
/// This is the seam: the OpenAI transform path dispatches to the normalizer matching a provider's
/// <see cref="RequestNormalization"/> opt-in. Adding a new normalization is a new implementation +
/// enum value, not a new branch in the router or forwarder (HLD 004 Key Goal 4).
/// </remarks>
internal interface IRequestNormalizer
{
    /// <summary>The opt-in profile this normalizer implements.</summary>
    RequestNormalization Kind { get; }

    /// <summary>Mutate the parsed request body in place. Must be idempotent.</summary>
    void Normalize(JsonObject root);
}
