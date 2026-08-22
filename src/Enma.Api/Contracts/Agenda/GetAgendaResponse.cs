namespace Enma.Api.Contracts.Agenda;

public sealed record GetAgendaResponse(
    IReadOnlyList<AgendaItemResponse> Items);
