namespace Enma.E2ETests.Infrastructure;

// A non-owner who accepted an invitation through the API and stays signed in.
public sealed class SeededMember : IDisposable
{
    private readonly SeededSession session;

    public SeededMember(
        SeededSession session,
        Guid organizationId,
        Guid membershipId,
        string name)
    {
        ArgumentNullException.ThrowIfNull(session);
        this.session = session;
        OrganizationId = organizationId;
        MembershipId = membershipId;
        Name = name;
    }

    public Guid OrganizationId { get; }

    public Guid MembershipId { get; }

    public string Name { get; }

    public Task<Guid> CreateIndividualClientAsync(string name)
    {
        return session.CreateAsync(
            $"api/organizations/{OrganizationId:D}/clients",
            new { name, email = (string?)null, phone = (string?)null, cpf = (string?)null });
    }

    public void Dispose()
    {
        session.Dispose();
    }
}
