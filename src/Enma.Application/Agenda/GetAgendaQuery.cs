namespace Enma.Application.Agenda;

public sealed record GetAgendaQuery(
    Guid UserId,
    Guid OrganizationId,
    DateTimeOffset From,
    DateTimeOffset To);
