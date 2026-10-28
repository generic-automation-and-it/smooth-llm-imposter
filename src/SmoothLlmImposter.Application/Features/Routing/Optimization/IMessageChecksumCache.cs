namespace SmoothLlmImposter.Application.Features.Routing.Optimization;

/// <summary>
/// Session-scoped memory of the text forwarded for each original human message (HLD 017 LADR-05).
/// The first value for a checksum is final; a hit makes no model call.
/// </summary>
public interface IMessageChecksumCache
{
    bool TryGet(
        SessionIdentity session,
        CallerHeaders headers,
        string originalText,
        string optimizerVersion,
        out string forwardedText);

    void Store(
        SessionIdentity session,
        CallerHeaders headers,
        string originalText,
        string optimizerVersion,
        string forwardedText);

    /// <summary>
    /// Returns the checksum key used to identify an original message in the cache (SHA-256 over the
    /// original text plus the optimizer version). Exposed for logging/diagnostics only.
    /// </summary>
    string Checksum(string originalText, string optimizerVersion);
}
