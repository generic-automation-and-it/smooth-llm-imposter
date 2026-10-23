using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace SmoothLlmImposter.Application.Features.Routing.Decisions;

internal sealed class DecisionAnswerCache(int capacity = 4096)
{
    private readonly ConcurrentDictionary<string, double> _entries = new();
    private readonly ConcurrentQueue<string> _order = new();

    public static string KeyFor(string model, string state, JsonObject question) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            new JsonArray(model, state, question.DeepClone()).ToJsonString())));

    public bool TryGet(string key, out double probability) => _entries.TryGetValue(key, out probability);

    public void Add(string key, double probability)
    {
        if (!_entries.TryAdd(key, probability))
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
