using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using SmoothLlmImposter.Application.Common.Clients.Optimizer;

namespace SmoothLlmImposter.Application.Features.Routing.Optimization;

internal sealed class MessageChecksumCache : IMessageChecksumCache
{
    private const string ScopeKind = "message-optimizer";
    private const int Uncapped = int.MaxValue; // SessionScopedMemory only evicts at its configured cap.

    private readonly SessionScopedMemory<Dictionary<string, string>> _memory;

    public MessageChecksumCache(TimeProvider timeProvider, IOptions<LlmMessagesOptions> options)
    {
        int ttlHours = options.Value.CacheTtlHours > 0 ? options.Value.CacheTtlHours : 120;
        _memory = new SessionScopedMemory<Dictionary<string, string>>(
            timeProvider,
            static () => new Dictionary<string, string>(StringComparer.Ordinal),
            TimeSpan.FromHours(ttlHours),
            Uncapped);
    }

    public bool TryGet(
        SessionIdentity session,
        CallerHeaders headers,
        string originalText,
        string optimizerVersion,
        out string forwardedText)
    {
        forwardedText = string.Empty;
        if (!IsCacheable(session))
        {
            return false;
        }

        string scope = ScopeKey(session, headers);
        string key = Checksum(originalText, optimizerVersion);
        string? cached = null;
        _memory.Use(scope, entries => cached = entries.GetValueOrDefault(key));
        if (cached is null)
        {
            return false;
        }

        forwardedText = cached;
        return true;
    }

    public void Store(
        SessionIdentity session,
        CallerHeaders headers,
        string originalText,
        string optimizerVersion,
        string forwardedText)
    {
        if (!IsCacheable(session))
        {
            return;
        }

        string scope = ScopeKey(session, headers);
        string key = Checksum(originalText, optimizerVersion);
        _memory.Use(scope, entries => entries.TryAdd(key, forwardedText));
    }

    private static bool IsCacheable(SessionIdentity session) =>
        session is { Source: SessionIdentitySource.Captured } && session.HasValue;

    private static string ScopeKey(SessionIdentity session, CallerHeaders headers) =>
        SessionScope.Key(ScopeKind, session.Value!, headers);

    private static string Checksum(string originalText, string optimizerVersion) =>
        SessionScope.Hash(new JsonArray(originalText, optimizerVersion).ToJsonString());
}
