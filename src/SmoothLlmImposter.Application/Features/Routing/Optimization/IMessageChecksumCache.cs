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
}
