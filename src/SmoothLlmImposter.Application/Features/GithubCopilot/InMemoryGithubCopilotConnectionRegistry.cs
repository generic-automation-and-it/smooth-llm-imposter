namespace SmoothLlmImposter.Application.Features.GithubCopilot;

/// <summary>
/// Thread-safe published-connection registry. Reads return the immutable record directly (it cannot be
/// mutated), so the inference path allocates nothing; writes replace the reference under a short lock.
/// </summary>
internal sealed class InMemoryGithubCopilotConnectionRegistry : IGithubCopilotConnectionRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, GithubCopilotPublishedConnection> _byPrefix = new(StringComparer.Ordinal);

    public void Publish(GithubCopilotPublishedConnection connection)
    {
        lock (_gate)
        {
            _byPrefix[connection.Prefix] = connection;
        }
    }

    public GithubCopilotPublishedConnection? GetByPrefix(string prefix)
    {
        lock (_gate)
        {
            return _byPrefix.GetValueOrDefault(prefix);
        }
    }

    public GithubCopilotPublishedConnection[] Snapshot()
    {
        lock (_gate)
        {
            return _byPrefix.Values.OrderBy(static connection => connection.Prefix, StringComparer.Ordinal).ToArray();
        }
    }

    public bool InvalidateIfCurrent(string prefix, long revision)
    {
        lock (_gate)
        {
            if (!_byPrefix.TryGetValue(prefix, out GithubCopilotPublishedConnection? current) ||
                current.Revision != revision)
            {
                return false;
            }

            Replace(current with { State = GithubCopilotConnectionState.ReauthenticationRequired });
            return true;
        }
    }

    private void Replace(GithubCopilotPublishedConnection connection) =>
        _byPrefix[connection.Prefix] = connection;
}
