namespace Enma.Application.Organizations.GetById;

public sealed class OrganizationNotFoundException : InvalidOperationException
{
    public OrganizationNotFoundException(Guid organizationId)
        : base($"Organization with id '{organizationId}' was not found.")
    {
        OrganizationId = organizationId;
    }

    public Guid OrganizationId { get; }
}
