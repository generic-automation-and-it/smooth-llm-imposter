using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SmoothLlmImposter.Application.Common.Persistence;
using SmoothLlmImposter.Domain.Credentials;
using SmoothLlmImposter.Domain.Routing;
using SmoothLlmImposter.Infrastructure.Persistence;

namespace SmoothLlmImposter.Infrastructure.Persistence.Stores;

internal sealed class CredentialStore(ImposterDbContext dbContext) : ICredentialStore
{
    public async Task<ProviderCredential> AddAsync(ProviderCredential credential, CancellationToken cancellationToken)
    {
        dbContext.ProviderCredentials.Add(credential);
        await dbContext.SaveChangesAsync(cancellationToken);
        return credential;
    }

    public async Task<IReadOnlyList<ProviderCredential>> ListAsync(CancellationToken cancellationToken) =>
        await dbContext.ProviderCredentials
            .OrderBy(c => EF.Property<string>(c, "ProviderDialect"))
            .ThenBy(c => c.Name)
            .ToArrayAsync(cancellationToken);

    public async Task<ProviderCredential?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        await dbContext.ProviderCredentials.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

    public async Task<ProviderCredential?> GetActiveAsync(ApiDialect dialect, CancellationToken cancellationToken) =>
        await dbContext.ProviderCredentials
            .FirstOrDefaultAsync(c => EF.Property<string>(c, "ProviderDialect") == dialect.ToToken() && c.IsActive, cancellationToken);

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        ProviderCredential? credential = await GetAsync(id, cancellationToken);
        if (credential is null)
        {
            return;
        }

        dbContext.ProviderCredentials.Remove(credential);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<ProviderCredential> UpdateAsync(ProviderCredential credential, CancellationToken cancellationToken)
    {
        await dbContext.SaveChangesAsync(cancellationToken);
        return credential;
    }

    public async Task<ProviderCredential> ActivateAsync(Guid id, CancellationToken cancellationToken)
    {
        ProviderCredential credential = await GetAsync(id, cancellationToken)
            ?? throw new InvalidOperationException($"Credential '{id}' was not found.");

        await using IDbContextTransaction? transaction = await BeginTransactionIfSupportedAsync(cancellationToken);

        string dialect = EF.Property<string>(credential, "ProviderDialect");
        ProviderCredential[] siblings = await dbContext.ProviderCredentials
            .Where(c => EF.Property<string>(c, "ProviderDialect") == dialect)
            .ToArrayAsync(cancellationToken);

        foreach (ProviderCredential sibling in siblings)
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

        await dbContext.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return credential;
    }

    private async Task<IDbContextTransaction?> BeginTransactionIfSupportedAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await dbContext.Database.BeginTransactionAsync(cancellationToken);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
