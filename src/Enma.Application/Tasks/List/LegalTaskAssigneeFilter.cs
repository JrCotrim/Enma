namespace Enma.Application.Tasks.List;

public sealed record LegalTaskAssigneeFilter(
    LegalTaskAssigneeFilterKind Kind,
    Guid? MembershipId = null)
{
    public static LegalTaskAssigneeFilter Any { get; } = new(
        LegalTaskAssigneeFilterKind.Any);

    public static LegalTaskAssigneeFilter Self { get; } = new(
        LegalTaskAssigneeFilterKind.Self);

    public static LegalTaskAssigneeFilter Unassigned { get; } = new(
        LegalTaskAssigneeFilterKind.Unassigned);

    public static LegalTaskAssigneeFilter Membership(Guid membershipId)
    {
        return new LegalTaskAssigneeFilter(
            LegalTaskAssigneeFilterKind.Membership,
            membershipId);
    }
}

public enum LegalTaskAssigneeFilterKind
{
    Any = 0,
    Self = 1,
    Unassigned = 2,
    Membership = 3
}
