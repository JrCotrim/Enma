namespace Enma.Domain.CalendarEvents;

public sealed class CalendarEvent
{
    private const int MaximumTitleLength = 150;
    private const int MaximumDescriptionLength = 2_000;
    private const int MaximumLocationLength = 255;

    public CalendarEvent(
        Guid organizationId,
        string title,
        string? description,
        DateTimeOffset startsAt,
        DateTimeOffset endsAt,
        string? location,
        Guid? clientId,
        Guid? processId,
        Guid? assigneeMembershipId,
        Guid createdByMembershipId,
        DateTimeOffset createdAt)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException(
                CalendarEventErrors.OrganizationIdRequired,
                nameof(organizationId));
        }

        if (createdByMembershipId == Guid.Empty)
        {
            throw new ArgumentException(
                CalendarEventErrors.CreatedByMembershipIdRequired,
                nameof(createdByMembershipId));
        }

        ValidateAssociation(clientId, processId);
        ValidateOptionalIdentifier(
            assigneeMembershipId,
            nameof(assigneeMembershipId),
            CalendarEventErrors.AssigneeMembershipIdInvalid);
        ValidateTimeRange(startsAt, endsAt);

        if (createdAt == DateTimeOffset.MinValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(createdAt),
                CalendarEventErrors.CreatedAtInvalid);
        }

        Id = Guid.NewGuid();
        OrganizationId = organizationId;
        Title = NormalizeTitle(title);
        Description = NormalizeDescription(description);
        StartsAt = startsAt.ToUniversalTime();
        EndsAt = endsAt.ToUniversalTime();
        Location = NormalizeLocation(location);
        ClientId = clientId;
        ProcessId = processId;
        AssigneeMembershipId = assigneeMembershipId;
        CreatedByMembershipId = createdByMembershipId;
        CreatedAt = createdAt.ToUniversalTime();
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public string Title { get; private set; }

    public string? Description { get; private set; }

    public DateTimeOffset StartsAt { get; private set; }

    public DateTimeOffset EndsAt { get; private set; }

    public string? Location { get; private set; }

    public Guid? ClientId { get; private set; }

    public Guid? ProcessId { get; private set; }

    public Guid? AssigneeMembershipId { get; private set; }

    public Guid CreatedByMembershipId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public void ChangeDetails(
        string title,
        string? description,
        string? location)
    {
        string normalizedTitle = NormalizeTitle(title);
        string? normalizedDescription = NormalizeDescription(description);
        string? normalizedLocation = NormalizeLocation(location);

        Title = normalizedTitle;
        Description = normalizedDescription;
        Location = normalizedLocation;
    }

    public void Reschedule(DateTimeOffset startsAt, DateTimeOffset endsAt)
    {
        ValidateTimeRange(startsAt, endsAt);

        StartsAt = startsAt.ToUniversalTime();
        EndsAt = endsAt.ToUniversalTime();
    }

    public void ChangeAssociation(Guid? clientId, Guid? processId)
    {
        ValidateAssociation(clientId, processId);

        ClientId = clientId;
        ProcessId = processId;
    }

    public void ChangeAssignee(Guid? assigneeMembershipId)
    {
        ValidateOptionalIdentifier(
            assigneeMembershipId,
            nameof(assigneeMembershipId),
            CalendarEventErrors.AssigneeMembershipIdInvalid);

        AssigneeMembershipId = assigneeMembershipId;
    }

    private static string NormalizeTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException(
                CalendarEventErrors.TitleRequired,
                nameof(title));
        }

        string normalizedTitle = title.Trim();

        if (normalizedTitle.Length > MaximumTitleLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(title),
                CalendarEventErrors.TitleTooLong);
        }

        return normalizedTitle;
    }

    private static string? NormalizeDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return null;
        }

        string normalizedDescription = description.Trim();

        if (normalizedDescription.Length > MaximumDescriptionLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(description),
                CalendarEventErrors.DescriptionTooLong);
        }

        return normalizedDescription;
    }

    private static string? NormalizeLocation(string? location)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            return null;
        }

        string normalizedLocation = location.Trim();

        if (normalizedLocation.Length > MaximumLocationLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(location),
                CalendarEventErrors.LocationTooLong);
        }

        return normalizedLocation;
    }

    private static void ValidateAssociation(Guid? clientId, Guid? processId)
    {
        ValidateOptionalIdentifier(
            clientId,
            nameof(clientId),
            CalendarEventErrors.ClientIdInvalid);
        ValidateOptionalIdentifier(
            processId,
            nameof(processId),
            CalendarEventErrors.ProcessIdInvalid);

        if (clientId.HasValue && processId.HasValue)
        {
            throw new ArgumentException(
                CalendarEventErrors.AssociationInvalid,
                nameof(processId));
        }
    }

    private static void ValidateTimeRange(
        DateTimeOffset startsAt,
        DateTimeOffset endsAt)
    {
        if (startsAt == DateTimeOffset.MinValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(startsAt),
                CalendarEventErrors.StartsAtInvalid);
        }

        if (endsAt == DateTimeOffset.MinValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(endsAt),
                CalendarEventErrors.EndsAtInvalid);
        }

        if (endsAt <= startsAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(endsAt),
                CalendarEventErrors.TimeRangeInvalid);
        }
    }

    private static void ValidateOptionalIdentifier(
        Guid? identifier,
        string parameterName,
        string error)
    {
        if (identifier == Guid.Empty)
        {
            throw new ArgumentException(error, parameterName);
        }
    }
}
