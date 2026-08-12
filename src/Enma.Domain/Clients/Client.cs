namespace Enma.Domain.Clients;

public sealed class Client
{
    private const int MaximumNameLength = 150;

    public Client(Guid organizationId, string name, DateTimeOffset createdAt)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException(
                ClientErrors.OrganizationIdRequired,
                nameof(organizationId));
        }

        if (createdAt == DateTimeOffset.MinValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(createdAt),
                ClientErrors.CreatedAtInvalid);
        }

        Id = Guid.NewGuid();
        OrganizationId = organizationId;
        Name = NormalizeName(name);
        IsActive = true;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public string Name { get; private set; }

    public bool IsActive { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public void ChangeName(string name)
    {
        Name = NormalizeName(name);
    }

    public void Activate()
    {
        IsActive = true;
    }

    public void Deactivate()
    {
        IsActive = false;
    }

    private static string NormalizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException(ClientErrors.NameRequired, nameof(name));
        }

        string normalizedName = name.Trim();

        if (normalizedName.Length > MaximumNameLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(name),
                ClientErrors.NameTooLong);
        }

        return normalizedName;
    }
}
