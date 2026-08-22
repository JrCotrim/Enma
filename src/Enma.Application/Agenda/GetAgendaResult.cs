namespace Enma.Application.Agenda;

public sealed class GetAgendaResult
{
    private GetAgendaResult(
        GetAgendaResultStatus status,
        IReadOnlyList<AgendaItemReadModel> items)
    {
        Status = status;
        Items = items;
    }

    public GetAgendaResultStatus Status { get; }

    public IReadOnlyList<AgendaItemReadModel> Items { get; }

    public static GetAgendaResult AccessDenied { get; } = new(
        GetAgendaResultStatus.AccessDenied,
        Array.Empty<AgendaItemReadModel>());

    public static GetAgendaResult InvalidInput { get; } = new(
        GetAgendaResultStatus.InvalidInput,
        Array.Empty<AgendaItemReadModel>());

    public static GetAgendaResult Succeeded(
        IReadOnlyList<AgendaItemReadModel> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        return new GetAgendaResult(
            GetAgendaResultStatus.Succeeded,
            items.ToArray());
    }
}

public enum GetAgendaResultStatus
{
    AccessDenied = 0,
    InvalidInput = 1,
    Succeeded = 2
}
