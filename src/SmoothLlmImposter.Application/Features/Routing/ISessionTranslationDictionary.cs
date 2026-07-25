namespace SmoothLlmImposter.Application.Features.Routing;

/// <summary>
/// Process-lifetime dictionary that maps caller-supplied session identifiers to synthetic
/// identifiers minted by the <c>--newsession</c> switch (HLD 010, LADR-06). The translation
/// is applied on the forward path: when a request's resolved session identity matches a key,
/// the outbound request carries the synthetic id instead. No eviction, no clear, no remove —
/// the map grows monotonically for the lifetime of the process (NFR-04).
/// </summary>
public interface ISessionTranslationDictionary
{
    /// <summary>
    /// Looks up the synthetic id previously minted for <paramref name="callerId"/>. Returns
    /// <c>true</c> and writes the synthetic id to <paramref name="syntheticId"/> when found;
    /// returns <c>false</c> and leaves <paramref name="syntheticId"/> <c>null</c> when no
    /// mapping exists.
    /// </summary>
    bool TryTranslate(string callerId, out string? syntheticId);

    /// <summary>
    /// Mints a new synthetic id for <paramref name="callerId"/> and stores the mapping. First-write
    /// wins: if a mapping for <paramref name="callerId"/> already exists, returns <c>false</c> and
    /// leaves the existing mapping untouched. Returns <c>true</c> when the mapping was newly inserted.
    /// The minted synthetic id is written to <paramref name="syntheticId"/> on success.
    /// </summary>
    bool TryAdd(string callerId, out string? syntheticId);
}
