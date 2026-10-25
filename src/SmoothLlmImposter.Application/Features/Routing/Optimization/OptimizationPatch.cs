using System.Text.Json.Nodes;

namespace SmoothLlmImposter.Application.Features.Routing.Optimization;

/// <summary>
/// A single replacement targeting one owned slice of an optimization request body.
/// <see cref="Pointer"/> is an RFC 6901 JSON pointer identifying the node to replace
/// (e.g. <c>/tools</c>, <c>/messages/0/content/0/text</c>); an empty pointer is invalid.
/// <see cref="Replacement"/> is the new value, or null to remove the node.
/// </summary>
public sealed record OptimizationPatch(string Pointer, JsonNode? Replacement);