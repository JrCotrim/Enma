using Enma.Domain.Clients;

namespace Enma.Application.Clients;

public interface IClientCreationPersistence
{
    Task PersistAsync(
        Client client,
        CancellationToken cancellationToken = default);
}
