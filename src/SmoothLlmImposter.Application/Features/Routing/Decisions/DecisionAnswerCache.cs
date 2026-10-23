using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace SmoothLlmImposter.Application.Features.Routing.Decisions;

internal sealed class DecisionAnswerCache(int capacity = 1024)
{
    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, double>> _entries = new();
    private readonly ConcurrentQueue<string> _order = new();

    public static string KeyFor(JsonObject request) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.ToJsonString())));

    public bool TryGet(string key, out IReadOnlyDictionary<string, double> probabilities) =>
        _entries.TryGetValue(key, out probabilities!);

    public void Add(string key, IReadOnlyDictionary<string, double> probabilities)
    {
        if (!_entries.TryAdd(key, probabilities))
        {
            return;
        }

        _order.Enqueue(key);
        while (_entries.Count > capacity && _order.TryDequeue(out string? oldest))
        {
            _entries.TryRemove(oldest, out _);
        }
    }
}
