using SmoothLlmImposter.Application.Common.Persistence;
using SmoothLlmImposter.Domain.Credentials;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Infrastructure.Persistence.Stores;

/// <summary>
/// No-op <see cref="ICredentialStore"/> used when no PostgreSQL connection string is configured. Stored
/// passthrough credentials (HLD 002) and the authorization override (HLD 003) are an optional add-on; the
/// router is stateless and key-less by default. Read lookups return "nothing" — so the catch-all passthrough
/// resolves a null credential and forwards the caller's own inbound auth — and mutations fail fast with a
/// clear message rather than silently succeeding against storage that does not exist.
/// </summary>
internal sealed class NullCredentialStore : ICredentialStore
{
    private const string NotConfigured =
        "Credential persistence is not configured. Set ConnectionStrings:ImposterDb to enable stored " +
        "passthrough credentials and the authorization override.";

    public Task<ProviderCredential> AddAsync(ProviderCredential credential, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(NotConfigured);

    public Task<IReadOnlyList<ProviderCredential>> ListAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ProviderCredential>>([]);

    public Task<ProviderCredential?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult<ProviderCredential?>(null);

    public Task<ProviderCredential?> GetActiveAsync(ApiDialect dialect, CancellationToken cancellationToken) =>
        Task.FromResult<ProviderCredential?>(null);

    public Task DeleteAsync(Guid id, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(NotConfigured);

    public Task<ProviderCredential> UpdateAsync(ProviderCredential credential, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(NotConfigured);

    public Task<ProviderCredential> ActivateAsync(Guid id, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(NotConfigured);
}
