using Enma.Domain.Processes;

namespace Enma.Application.Processes;

public interface ILegalProcessCreationPersistence
{
    Task PersistAsync(
        LegalProcess legalProcess,
        CancellationToken cancellationToken = default);
}
