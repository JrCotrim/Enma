using Enma.Application.Validation;

namespace Enma.Application.Deadlines.List;

internal sealed record LegalDeadlineResponsibleFilter(
    LegalDeadlineResponsibleFilterKind Kind,
    Guid? MembershipId = null)
{
    public static LegalDeadlineResponsibleFilter Any { get; } = new(
        LegalDeadlineResponsibleFilterKind.Any);

    public static LegalDeadlineResponsibleFilter Self { get; } = new(
        LegalDeadlineResponsibleFilterKind.Self);

    public static LegalDeadlineResponsibleFilter Unassigned { get; } = new(
        LegalDeadlineResponsibleFilterKind.Unassigned);

    public static LegalDeadlineResponsibleFilter Parse(string? value)
    {
        if (value is null ||
            string.Equals(value, "any", StringComparison.OrdinalIgnoreCase))
        {
            return Any;
        }

        if (string.Equals(value, "self", StringComparison.OrdinalIgnoreCase))
        {
            return Self;
        }

        if (string.Equals(value, "unassigned", StringComparison.OrdinalIgnoreCase))
        {
            return Unassigned;
        }

        if (Guid.TryParseExact(value, "D", out Guid membershipId) &&
            membershipId != Guid.Empty)
        {
            return new LegalDeadlineResponsibleFilter(
                LegalDeadlineResponsibleFilterKind.Membership,
                membershipId);
        }

        throw new RequestValidationException(
            "Responsible must be 'any', 'self', 'unassigned', or a membership identifier.");
    }
}

internal enum LegalDeadlineResponsibleFilterKind
{
    Any = 0,
    Self = 1,
    Unassigned = 2,
    Membership = 3
}
