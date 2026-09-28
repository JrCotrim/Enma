namespace Enma.Domain.Processes;

public sealed class LegalProcess
{
    private const int MaximumTitleLength = 150;
    private const int MaximumProcessNumberLength = 100;
    private const int MaximumCourtOrAuthorityLength = 200;

    public LegalProcess(
        Guid organizationId,
        Guid clientId,
        string title,
        DateTimeOffset createdAt,
        string? processNumber = null,
        string? courtOrAuthority = null,
        Guid? responsibleMembershipId = null)
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
        (ProcessNumber, NormalizedProcessNumber) =
            NormalizeProcessNumber(processNumber);
        Status = LegalProcessStatus.InProgress;
        CourtOrAuthority = NormalizeCourtOrAuthority(courtOrAuthority);
        ResponsibleMembershipId = responsibleMembershipId;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid ClientId { get; private set; }

    public string Title { get; private set; }

    public string? ProcessNumber { get; private set; }

    public string? NormalizedProcessNumber { get; private set; }

    public LegalProcessStatus Status { get; private set; }

    public string? CourtOrAuthority { get; private set; }

    public Guid? ResponsibleMembershipId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public void ChangeTitle(string title)
    {
        Title = NormalizeTitle(title);
    }

    public void ChangeStatus(LegalProcessStatus status)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(
                nameof(status),
                LegalProcessErrors.StatusInvalid);
        }

        if (status == Status)
        {
            return;
        }

        bool isAllowed = (Status, status) switch
        {
            (LegalProcessStatus.InProgress, LegalProcessStatus.Suspended) => true,
            (LegalProcessStatus.InProgress, LegalProcessStatus.Closed) => true,
            (LegalProcessStatus.Suspended, LegalProcessStatus.InProgress) => true,
            (LegalProcessStatus.Suspended, LegalProcessStatus.Closed) => true,
            (LegalProcessStatus.Closed, LegalProcessStatus.InProgress) => true,
            _ => false
        };

        if (!isAllowed)
        {
            throw new InvalidOperationException(
                LegalProcessErrors.StatusTransitionInvalid);
        }

        Status = status;
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

    private static (string? Display, string? Normalized) NormalizeProcessNumber(
        string? processNumber)
    {
        if (string.IsNullOrWhiteSpace(processNumber))
        {
            return (null, null);
        }

        string display = processNumber.Trim();

        if (display.Length > MaximumProcessNumberLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(processNumber),
                LegalProcessErrors.ProcessNumberTooLong);
        }

        string cnjCandidate = string.Concat(display.Where(character =>
            character is not ('.' or '-' or '/') &&
            !char.IsWhiteSpace(character)));
        string normalized = cnjCandidate.Length == 20 &&
            cnjCandidate.All(character => character is >= '0' and <= '9')
                ? cnjCandidate
                : string.Join(
                    ' ',
                    display.Split(
                        (char[]?)null,
                        StringSplitOptions.RemoveEmptyEntries))
                    .ToUpperInvariant();

        return (display, normalized);
    }

    private static string? NormalizeCourtOrAuthority(string? courtOrAuthority)
    {
        if (string.IsNullOrWhiteSpace(courtOrAuthority))
        {
            return null;
        }

        string normalized = courtOrAuthority.Trim();

        if (normalized.Length > MaximumCourtOrAuthorityLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(courtOrAuthority),
                LegalProcessErrors.CourtOrAuthorityTooLong);
        }

        return normalized;
    }
}
