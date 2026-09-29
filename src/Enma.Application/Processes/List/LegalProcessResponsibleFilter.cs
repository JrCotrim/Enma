using Enma.Application.Validation;

namespace Enma.Application.Processes.List;

internal sealed record LegalProcessResponsibleFilter(
    LegalProcessResponsibleFilterKind Kind,
    Guid? MembershipId = null)
{
    public static LegalProcessResponsibleFilter Any { get; } = new(
        LegalProcessResponsibleFilterKind.Any);

    public static LegalProcessResponsibleFilter Self { get; } = new(
        LegalProcessResponsibleFilterKind.Self);

    public static LegalProcessResponsibleFilter Unassigned { get; } = new(
        LegalProcessResponsibleFilterKind.Unassigned);

    public static LegalProcessResponsibleFilter Parse(string? value)
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
            return new LegalProcessResponsibleFilter(
                LegalProcessResponsibleFilterKind.Membership,
                membershipId);
        }

        throw new RequestValidationException(
            "Responsible must be 'any', 'self', 'unassigned', or a membership identifier.");
    }
}

internal enum LegalProcessResponsibleFilterKind
{
    Any = 0,
    Self = 1,
    Unassigned = 2,
    Membership = 3
}
