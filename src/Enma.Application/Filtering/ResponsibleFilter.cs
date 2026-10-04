using Enma.Application.Validation;

namespace Enma.Application.Filtering;

internal sealed record ResponsibleFilter(
    ResponsibleFilterKind Kind,
    Guid? MembershipId = null)
{
    public static ResponsibleFilter Any { get; } = new(
        ResponsibleFilterKind.Any);

    public static ResponsibleFilter Self { get; } = new(
        ResponsibleFilterKind.Self);

    public static ResponsibleFilter Unassigned { get; } = new(
        ResponsibleFilterKind.Unassigned);

    public static ResponsibleFilter Parse(string? value)
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
            return new ResponsibleFilter(
                ResponsibleFilterKind.Membership,
                membershipId);
        }

        throw new RequestValidationException(
            "Responsible must be 'any', 'self', 'unassigned', or a membership identifier.");
    }
}

internal enum ResponsibleFilterKind
{
    Any = 0,
    Self = 1,
    Unassigned = 2,
    Membership = 3
}
