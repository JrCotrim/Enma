namespace Enma.Domain.Processes;

public sealed class LegalProcess
{
    private const int MaximumTitleLength = 150;

    public LegalProcess(
        Guid organizationId,
        Guid clientId,
        string title,
        DateTimeOffset createdAt)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException(
                LegalProcessErrors.OrganizationIdRequired,
                nameof(organizationId));
        }

        if (clientId == Guid.Empty)
        {
            throw new ArgumentException(
                LegalProcessErrors.ClientIdRequired,
                nameof(clientId));
        }

        if (createdAt == DateTimeOffset.MinValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(createdAt),
                LegalProcessErrors.CreatedAtInvalid);
        }

        Id = Guid.NewGuid();
        OrganizationId = organizationId;
        ClientId = clientId;
        Title = NormalizeTitle(title);
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid ClientId { get; private set; }

    public string Title { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public void ChangeTitle(string title)
    {
        Title = NormalizeTitle(title);
    }

    private static string NormalizeTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException(
                LegalProcessErrors.TitleRequired,
                nameof(title));
        }

        string normalizedTitle = title.Trim();

        if (normalizedTitle.Length > MaximumTitleLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(title),
                LegalProcessErrors.TitleTooLong);
        }

        return normalizedTitle;
    }
}
