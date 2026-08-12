using System.Data;
using Enma.Application.Clients;
using Enma.Domain.Clients;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Enma.Infrastructure.Persistence;

public sealed class ClientMutationPersistence : IClientMutationPersistence
{
    private readonly DbContextOptions<EnmaDbContext> _dbContextOptions;

    public ClientMutationPersistence(DbContextOptions<EnmaDbContext> dbContextOptions)
    {
        ArgumentNullException.ThrowIfNull(dbContextOptions);
        _dbContextOptions = dbContextOptions;
    }

    public Task<ClientMutationPersistenceResult> UpdateNameAsync(
        Guid clientId,
        Guid organizationId,
        string name,
        CancellationToken cancellationToken = default)
    {
        return MutateAsync(
            clientId,
            organizationId,
            client => client.ChangeName(name),
            cancellationToken);
    }

    public Task<ClientMutationPersistenceResult> DeactivateAsync(
        Guid clientId,
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        return MutateAsync(
            clientId,
            organizationId,
            client => client.Deactivate(),
            cancellationToken);
    }

    public Task<ClientMutationPersistenceResult> ReactivateAsync(
        Guid clientId,
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        return MutateAsync(
            clientId,
            organizationId,
            client => client.Activate(),
            cancellationToken);
    }

    private async Task<ClientMutationPersistenceResult> MutateAsync(
        Guid clientId,
        Guid organizationId,
        Action<Client> mutation,
        CancellationToken cancellationToken)
    {
        await using var dbContext = new EnmaDbContext(_dbContextOptions);
        await using IDbContextTransaction transaction =
            await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

        Client? client = (await dbContext.Clients
                .FromSqlInterpolated(
                    $"""
                    SELECT * FROM clients
                    WHERE id = {clientId}
                      AND organization_id = {organizationId}
                    FOR UPDATE
                    """)
                .ToListAsync(cancellationToken))
            .SingleOrDefault();

        if (client is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return ClientMutationPersistenceResult.NotFound;
        }

        mutation(client);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return ClientMutationPersistenceResult.Succeeded;
    }
}
