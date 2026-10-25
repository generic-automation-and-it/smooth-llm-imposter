namespace SmoothLlmImposter.Application.Features.Routing;

internal sealed class SessionScopedMemory<T>(
    TimeProvider timeProvider,
    Func<T> create,
    TimeSpan? idleLifetime = null,
    int? maxScopes = null) where T : class
{
    internal const int MaxScopes = 256;
    internal static readonly TimeSpan IdleLifetime = TimeSpan.FromMinutes(30);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _scopes = new(StringComparer.Ordinal);
    private readonly TimeSpan _idleLifetime = idleLifetime ?? IdleLifetime;
    private readonly int? _maxScopes = maxScopes is null or > 0
        ? maxScopes ?? MaxScopes
        : throw new ArgumentOutOfRangeException(nameof(maxScopes), "maxScopes must be positive.");

    public TResult Use<TResult>(string scope, Func<T, TResult> use)
    {
        lock (_gate)
        {
            DateTimeOffset now = timeProvider.GetUtcNow();
            RemoveExpired(now);
            if (!_scopes.TryGetValue(scope, out Entry? entry))
            {
                if (_maxScopes is int limit && _scopes.Count >= limit)
                {
                    _scopes.Remove(_scopes.MinBy(pair => pair.Value.LastUsed).Key);
                }
                entry = new Entry(create());
                _scopes.Add(scope, entry);
            }

            entry.LastUsed = now;
            return use(entry.Value);
        }
    }

    public void Use(string scope, Action<T> use) => Use(scope, value =>
    {
        use(value);
        return true;
    });

    private void RemoveExpired(DateTimeOffset now)
    {
        string[] expired = _scopes.Where(pair => now - pair.Value.LastUsed >= _idleLifetime)
            .Select(pair => pair.Key).ToArray();
        foreach (string scope in expired)
        {
            _scopes.Remove(scope);
        }
    }

    private sealed class Entry(T value)
    {
        public T Value { get; } = value;

        public DateTimeOffset LastUsed { get; set; }
    }
}
