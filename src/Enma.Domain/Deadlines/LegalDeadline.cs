namespace Enma.Domain.Deadlines;

public sealed class LegalDeadline
{
    private const int MaximumTitleLength = 150;

    public LegalDeadline(
        Guid organizationId,
        Guid processId,
        string title,
        DateOnly dueDate,
        DateTimeOffset createdAt)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException(
                LegalDeadlineErrors.OrganizationIdRequired,
                nameof(organizationId));
        }

        if (processId == Guid.Empty)
        {
            throw new ArgumentException(
                LegalDeadlineErrors.ProcessIdRequired,
                nameof(processId));
        }

        if (dueDate == DateOnly.MinValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(dueDate),
                LegalDeadlineErrors.DueDateInvalid);
        }

        if (createdAt == DateTimeOffset.MinValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(createdAt),
                LegalDeadlineErrors.CreatedAtInvalid);
        }

        Id = Guid.NewGuid();
        OrganizationId = organizationId;
        ProcessId = processId;
        Title = NormalizeTitle(title);
        DueDate = dueDate;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid ProcessId { get; private set; }

    public string Title { get; private set; }

    public DateOnly DueDate { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public void Complete(DateTimeOffset completedAt)
    {
        if (completedAt == DateTimeOffset.MinValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(completedAt),
                LegalDeadlineErrors.CompletedAtInvalid);
        }

        if (completedAt < CreatedAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(completedAt),
                LegalDeadlineErrors.CompletionBeforeCreation);
        }

        CompletedAt ??= completedAt;
    }

    public void Reopen()
    {
        CompletedAt = null;
    }

    public void ChangeDetails(string title, DateOnly dueDate)
    {
        if (CompletedAt is not null)
        {
            throw new InvalidOperationException(
                LegalDeadlineErrors.CompletedDeadlineDetailsCannotChange);
        }

        string normalizedTitle = NormalizeTitle(title);

        if (dueDate == DateOnly.MinValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(dueDate),
                LegalDeadlineErrors.DueDateInvalid);
        }

        Title = normalizedTitle;
        DueDate = dueDate;
    }

    private static string NormalizeTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException(
                LegalDeadlineErrors.TitleRequired,
                nameof(title));
        }

        string normalizedTitle = title.Trim();

        if (normalizedTitle.Length > MaximumTitleLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(title),
                LegalDeadlineErrors.TitleTooLong);
        }

        return normalizedTitle;
    }
}
