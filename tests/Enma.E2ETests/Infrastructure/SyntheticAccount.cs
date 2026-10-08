namespace Enma.E2ETests.Infrastructure;

// Unique, obviously synthetic identities per run: nothing resembles real PII
// and the per-recipient e-mail budget never accumulates across journeys.
public sealed record SyntheticAccount(
    string OrganizationName,
    string OrganizationSlug,
    string OwnerName,
    string Email,
    string Password)
{
    public static SyntheticAccount Create(string label)
    {
        string uniqueId = Guid.NewGuid().ToString("N");

        return new SyntheticAccount(
            OrganizationName: $"Escritório {label} E2E {uniqueId[..8]}",
            OrganizationSlug: $"e2e-{uniqueId}",
            OwnerName: $"Titular {label} E2E",
            Email: $"e2e-{uniqueId}@example.test",
            Password: $"E2e-{Guid.NewGuid():N}-Aa1");
    }
}
