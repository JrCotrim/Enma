namespace Enma.E2ETests.Infrastructure;

// Synthetic person invited into an existing organization. The name carries a
// unique suffix so journeys can pick the person out of member lookups.
public sealed record SyntheticInvitee(
    string Name,
    string Email,
    string Password)
{
    public static SyntheticInvitee Create(string label)
    {
        string uniqueId = Guid.NewGuid().ToString("N");

        return new SyntheticInvitee(
            Name: $"Integrante {label} E2E {uniqueId[..6]}",
            Email: $"e2e-{uniqueId}@example.test",
            Password: $"E2e-{Guid.NewGuid():N}-Aa1");
    }
}
