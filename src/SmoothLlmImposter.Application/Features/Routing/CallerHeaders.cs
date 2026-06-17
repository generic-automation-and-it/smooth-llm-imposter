namespace SmoothLlmImposter.Application.Features.Routing;

/// <summary>
/// The caller's full inbound header set, captured at the Host edge and forwarded to the upstream so the
/// router behaves as a transparent proxy: the request is relayed unchanged except for two things — the
/// <b>auth</b> header (the caller's own credential is forwarded on key-less passthrough, or replaced by the
/// provider key / a stored credential / the HLD-003 force-Bearer override) and <b>routing</b> (target URL +
/// the imposter model rewrite). Hop-by-hop and content headers are dropped by the forwarder; everything else
/// (e.g. <c>anthropic-beta</c>, <c>anthropic-version</c>, vendor <c>x-*</c> headers) passes through verbatim.
/// </summary>
public sealed record CallerHeaders(IReadOnlyList<KeyValuePair<string, IReadOnlyList<string>>> Items)
{
    public static readonly CallerHeaders None = new([]);

    /// <summary>Returns the values for <paramref name="name"/> (case-insensitive), or null if absent.</summary>
    public IReadOnlyList<string>? Get(string name)
    {
        foreach (KeyValuePair<string, IReadOnlyList<string>> item in Items)
        {
            if (string.Equals(item.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return item.Value;
            }
        }

        return null;
    }
}
