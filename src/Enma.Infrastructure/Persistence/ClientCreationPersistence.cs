using Enma.Application.Clients;
using Enma.Domain.Clients;

namespace Enma.Infrastructure.Persistence;

public sealed class ClientCreationPersistence : IClientCreationPersistence
{
    private readonly EnmaDbContext _dbContext;

    public ClientCreationPersistence(EnmaDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    public async Task PersistAsync(
        Client client,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        await _dbContext.Clients.AddAsync(client, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
