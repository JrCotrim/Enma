namespace Enma.Application.Agenda;

public interface IAgendaReadQueries
{
    Task<IReadOnlyList<AgendaItemReadModel>> ReadAsync(
        AgendaReadRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record AgendaReadRequest(
    Guid OrganizationId,
    DateOnly LocalStartDate,
    DateOnly LocalEndDate,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc);
