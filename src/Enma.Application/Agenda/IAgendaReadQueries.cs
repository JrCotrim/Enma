namespace Enma.Application.Agenda;

public interface IAgendaReadQueries
{
    Task<IReadOnlyList<AgendaItemReadModel>> ReadAsync(
        AgendaReadRequest request,
        CancellationToken cancellationToken = default);

    Task<UpcomingAgendaReadModel> ReadUpcomingAsync(
        UpcomingAgendaReadRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record AgendaReadRequest(
    Guid OrganizationId,
    DateOnly LocalStartDate,
    DateOnly LocalEndDate,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc);

public sealed record UpcomingAgendaReadRequest(
    Guid OrganizationId,
    DateOnly ReferenceDate,
    DateOnly ThroughDate,
    DateTimeOffset NowUtc,
    DateTimeOffset EventWindowEndUtc);
