using Enma.Application.Authorization;
using Enma.Domain.Organizations;

namespace Enma.Application.Agenda;

public sealed class GetAgendaUseCase
{
    public const int MaximumCalendarDays = 93;

    private readonly OrganizationAccessAuthorization _accessAuthorization;
    private readonly IAgendaReadQueries _readQueries;

    public GetAgendaUseCase(
        OrganizationAccessAuthorization accessAuthorization,
        IAgendaReadQueries readQueries)
    {
        ArgumentNullException.ThrowIfNull(accessAuthorization);
        ArgumentNullException.ThrowIfNull(readQueries);

        _accessAuthorization = accessAuthorization;
        _readQueries = readQueries;
    }

    public async Task<GetAgendaResult> ExecuteAsync(
        GetAgendaQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (!await HasViewAccessAsync(query, cancellationToken))
        {
            return GetAgendaResult.AccessDenied;
        }

        if (!TryCreateReadRequest(query, out AgendaReadRequest request))
        {
            return GetAgendaResult.InvalidInput;
        }

        IReadOnlyList<AgendaItemReadModel> items = await _readQueries.ReadAsync(
            request,
            cancellationToken);

        // Kind-first ordering avoids inventing a cross-type instant for DateOnly
        // items. Each kind then uses only its real temporal fields and Id.
        AgendaItemReadModel[] orderedItems = items
            .OrderBy(item => item.Kind)
            .ThenBy(item => item.Date)
            .ThenBy(item => item.StartsAt)
            .ThenBy(item => item.EndsAt)
            .ThenBy(item => item.Id)
            .ToArray();

        return GetAgendaResult.Succeeded(orderedItems);
    }

    private async Task<bool> HasViewAccessAsync(
        GetAgendaQuery query,
        CancellationToken cancellationToken)
    {
        OrganizationAccessAuthorizationResult access;

        try
        {
            access = await _accessAuthorization.AuthorizeAsync(
                query.UserId,
                query.OrganizationId,
                cancellationToken);
        }
        catch (ArgumentOutOfRangeException exception) when (
            exception.ParamName == "role")
        {
            return false;
        }

        return access.Status == OrganizationAccessAuthorizationStatus.Allowed &&
            access.UserId == query.UserId &&
            access.OrganizationId == query.OrganizationId &&
            access.MembershipId is Guid &&
            access.Role is OrganizationRole.Owner or
                OrganizationRole.Administrator or
                OrganizationRole.Member;
    }

    private static bool TryCreateReadRequest(
        GetAgendaQuery query,
        out AgendaReadRequest request)
    {
        request = null!;

        if (query.From == DateTimeOffset.MinValue ||
            query.To == DateTimeOffset.MinValue ||
            query.To <= query.From ||
            query.From.TimeOfDay != TimeSpan.Zero ||
            query.To.TimeOfDay != TimeSpan.Zero)
        {
            return false;
        }

        var localStartDate = new DateOnly(
            query.From.Year,
            query.From.Month,
            query.From.Day);
        var localEndDate = new DateOnly(
            query.To.Year,
            query.To.Month,
            query.To.Day);
        int calendarDays = localEndDate.DayNumber - localStartDate.DayNumber;

        if (calendarDays <= 0 || calendarDays > MaximumCalendarDays)
        {
            return false;
        }

        request = new AgendaReadRequest(
            query.OrganizationId,
            localStartDate,
            localEndDate,
            query.From.ToUniversalTime(),
            query.To.ToUniversalTime());
        return true;
    }
}
