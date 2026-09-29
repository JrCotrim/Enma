using Enma.Domain.Auditing;

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

        if (responsibleMembershipId == Guid.Empty)
        {
            throw new ArgumentException(
                LegalProcessErrors.ResponsibleMembershipIdInvalid,
                nameof(responsibleMembershipId));
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

    public static string? ToProcessNumberDigitsSearchTerm(string? searchTerm)
    {
        if (searchTerm is null)
        {
            return null;
        }

        string digits = string.Concat(searchTerm.Where(character =>
            character is not ('.' or '-' or '/') &&
            !char.IsWhiteSpace(character)));

        return digits.Length > 0 &&
            digits.All(character => character is >= '0' and <= '9')
                ? digits
                : null;
    }

    public void ChangeTitle(string title)
    {
        Title = NormalizeTitle(title);
    }

    public IReadOnlyList<LegalProcessChangedField> ChangeDetails(
        string? processNumber,
        string? courtOrAuthority)
    {
        (string? display, string? normalized) =
            NormalizeProcessNumber(processNumber);
        string? normalizedCourtOrAuthority =
            NormalizeCourtOrAuthority(courtOrAuthority);
        var changedFields = new List<LegalProcessChangedField>(2);

        if (!StringComparer.Ordinal.Equals(ProcessNumber, display))
        {
            changedFields.Add(LegalProcessChangedField.ProcessNumber);
        }

        if (!StringComparer.Ordinal.Equals(
                CourtOrAuthority,
                normalizedCourtOrAuthority))
        {
            changedFields.Add(LegalProcessChangedField.CourtOrAuthority);
        }

        ProcessNumber = display;
        NormalizedProcessNumber = normalized;
        CourtOrAuthority = normalizedCourtOrAuthority;

        return changedFields.AsReadOnly();
    }

    public bool ChangeResponsible(Guid? responsibleMembershipId)
    {
        if (responsibleMembershipId == Guid.Empty)
        {
            throw new ArgumentException(
                LegalProcessErrors.ResponsibleMembershipIdInvalid,
                nameof(responsibleMembershipId));
        }

        if (responsibleMembershipId == ResponsibleMembershipId)
        {
            return false;
        }

        ResponsibleMembershipId = responsibleMembershipId;
        return true;
    }

    public bool CanChangeStatusTo(LegalProcessStatus status)
    {
        return Enum.IsDefined(status) &&
            (status == Status || IsStatusTransitionAllowed(Status, status));
    }

    public bool ChangeStatus(LegalProcessStatus status)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(
                nameof(status),
                LegalProcessErrors.StatusInvalid);
        }

        if (status == Status)
        {
            return false;
        }

        if (!IsStatusTransitionAllowed(Status, status))
        {
            throw new InvalidOperationException(
                LegalProcessErrors.StatusTransitionInvalid);
        }

        Status = status;
        return true;
    }

    private static bool IsStatusTransitionAllowed(
        LegalProcessStatus currentStatus,
        LegalProcessStatus newStatus)
    {
        return (currentStatus, newStatus) switch
        {
            (LegalProcessStatus.InProgress, LegalProcessStatus.Suspended) => true,
            (LegalProcessStatus.InProgress, LegalProcessStatus.Closed) => true,
            (LegalProcessStatus.Suspended, LegalProcessStatus.InProgress) => true,
            (LegalProcessStatus.Suspended, LegalProcessStatus.Closed) => true,
            (LegalProcessStatus.Closed, LegalProcessStatus.InProgress) => true,
            _ => false
        };
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
