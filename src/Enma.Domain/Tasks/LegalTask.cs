namespace Enma.Domain.Tasks;

public sealed class LegalTask
{
    private const int MaximumTitleLength = 150;
    private const int MaximumDescriptionLength = 2_000;

    public LegalTask(
        Guid organizationId,
        string title,
        string? description,
        DateOnly? dueDate,
        Guid? processId,
        Guid? assigneeMembershipId,
        Guid createdByMembershipId,
        DateTimeOffset createdAt)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException(
                LegalTaskErrors.OrganizationIdRequired,
                nameof(organizationId));
        }

        if (createdByMembershipId == Guid.Empty)
        {
            throw new ArgumentException(
                LegalTaskErrors.CreatedByMembershipIdRequired,
                nameof(createdByMembershipId));
        }

        ValidateOptionalIdentifier(
            processId,
            nameof(processId),
            LegalTaskErrors.ProcessIdInvalid);
        ValidateOptionalIdentifier(
            assigneeMembershipId,
            nameof(assigneeMembershipId),
            LegalTaskErrors.AssigneeMembershipIdInvalid);
        ValidateDueDate(dueDate);

        if (createdAt == DateTimeOffset.MinValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(createdAt),
                LegalTaskErrors.CreatedAtInvalid);
        }

        Id = Guid.NewGuid();
        OrganizationId = organizationId;
        Title = NormalizeTitle(title);
        Description = NormalizeDescription(description);
        DueDate = dueDate;
        ProcessId = processId;
        AssigneeMembershipId = assigneeMembershipId;
        CreatedByMembershipId = createdByMembershipId;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public string Title { get; private set; }

    public string? Description { get; private set; }

    public DateOnly? DueDate { get; private set; }

    public Guid? ProcessId { get; private set; }

    public Guid? AssigneeMembershipId { get; private set; }

    public Guid CreatedByMembershipId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public void ChangeDetails(
        string title,
        string? description,
        DateOnly? dueDate,
        Guid? processId)
    {
        EnsurePending();

        string normalizedTitle = NormalizeTitle(title);
        string? normalizedDescription = NormalizeDescription(description);
        ValidateDueDate(dueDate);
        ValidateOptionalIdentifier(
            processId,
            nameof(processId),
            LegalTaskErrors.ProcessIdInvalid);

        Title = normalizedTitle;
        Description = normalizedDescription;
        DueDate = dueDate;
        ProcessId = processId;
    }

    public void ChangeAssignee(Guid? assigneeMembershipId)
    {
        EnsurePending();
        ValidateOptionalIdentifier(
            assigneeMembershipId,
            nameof(assigneeMembershipId),
            LegalTaskErrors.AssigneeMembershipIdInvalid);

        AssigneeMembershipId = assigneeMembershipId;
    }

    public void Complete(DateTimeOffset completedAt)
    {
        if (completedAt == DateTimeOffset.MinValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(completedAt),
                LegalTaskErrors.CompletedAtInvalid);
        }

        if (completedAt < CreatedAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(completedAt),
                LegalTaskErrors.CompletionBeforeCreation);
        }

        CompletedAt ??= completedAt;
    }

    public void Reopen()
    {
        CompletedAt = null;
    }

    private static string NormalizeTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException(
                LegalTaskErrors.TitleRequired,
                nameof(title));
        }

        string normalizedTitle = title.Trim();

        if (normalizedTitle.Length > MaximumTitleLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(title),
                LegalTaskErrors.TitleTooLong);
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
                LegalTaskErrors.DescriptionTooLong);
        }

        return normalizedDescription;
    }

    private static void ValidateDueDate(DateOnly? dueDate)
    {
        if (dueDate == DateOnly.MinValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(dueDate),
                LegalTaskErrors.DueDateInvalid);
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

    private void EnsurePending()
    {
        if (CompletedAt is not null)
        {
            throw new LegalTaskCompletedMutationException();
        }
    }
}
