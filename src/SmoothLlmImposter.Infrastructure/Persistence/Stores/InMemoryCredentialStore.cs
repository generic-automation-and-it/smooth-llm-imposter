using SmoothLlmImposter.Application.Common.Persistence;
using SmoothLlmImposter.Domain.Credentials;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Infrastructure.Persistence.Stores;

internal sealed class InMemoryCredentialStore : ICredentialStore
{
    private readonly object _gate = new();
    private readonly List<ProviderCredential> _credentials = [];

    public Task<ProviderCredential> AddAsync(ProviderCredential credential, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _credentials.Add(credential);
            return Task.FromResult(credential);
        }
    }

    public Task<IReadOnlyList<ProviderCredential>> ListAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<ProviderCredential>>(
                _credentials
                    .OrderBy(c => c.ProviderDialect, StringComparer.Ordinal)
                    .ThenBy(c => c.ProviderName, StringComparer.Ordinal)
                    .ThenBy(c => c.Name, StringComparer.Ordinal)
                    .ToArray());
        }
    }

    public Task<ProviderCredential?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_credentials.FirstOrDefault(c => c.Id == id));
        }
    }

    public Task<ProviderCredential?> GetActiveAsync(ApiDialect dialect, string providerName, CancellationToken cancellationToken)
    {
        string dialectToken = dialect.ToToken();
        lock (_gate)
        {
            return Task.FromResult(_credentials.FirstOrDefault(c =>
                c.ProviderDialect == dialectToken &&
                string.Equals(c.ProviderName, providerName, StringComparison.OrdinalIgnoreCase) &&
                c.IsActive));
        }
    }

    public Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _credentials.RemoveAll(c => c.Id == id);
            return Task.CompletedTask;
        }
    }

    public Task<ProviderCredential> UpdateAsync(ProviderCredential credential, CancellationToken cancellationToken) =>
        Task.FromResult(credential);

    public Task<ProviderCredential> ActivateAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ProviderCredential credential = _credentials.SingleOrDefault(c => c.Id == id)
                ?? throw new InvalidOperationException($"Credential '{id}' was not found.");

            foreach (ProviderCredential sibling in _credentials.Where(c =>
                c.ProviderDialect == credential.ProviderDialect &&
                string.Equals(c.ProviderName, credential.ProviderName, StringComparison.OrdinalIgnoreCase)))
            {
                if (sibling.Id == credential.Id)
                {
                    sibling.Activate();
                }
                else
                {
                    sibling.Deactivate();
                }
            }

            return Task.FromResult(credential);
        }
    }
}
