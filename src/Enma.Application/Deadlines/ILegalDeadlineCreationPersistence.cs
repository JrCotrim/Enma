using Enma.Domain.Deadlines;

namespace Enma.Application.Deadlines;

public interface ILegalDeadlineCreationPersistence
{
    Task PersistAsync(
        LegalDeadline legalDeadline,
        CancellationToken cancellationToken = default);
}
