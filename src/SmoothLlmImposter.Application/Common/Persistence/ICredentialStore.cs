using SmoothLlmImposter.Domain.Credentials;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.Common.Persistence;

public interface ICredentialStore
{
    Task<ProviderCredential> AddAsync(ProviderCredential credential, CancellationToken cancellationToken);

    Task<IReadOnlyList<ProviderCredential>> ListAsync(CancellationToken cancellationToken);

    Task<ProviderCredential?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<ProviderCredential?> GetActiveAsync(ApiDialect dialect, string providerName, CancellationToken cancellationToken);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken);

    Task<ProviderCredential> UpdateAsync(ProviderCredential credential, CancellationToken cancellationToken);

    Task<ProviderCredential> ActivateAsync(Guid id, CancellationToken cancellationToken);
}
