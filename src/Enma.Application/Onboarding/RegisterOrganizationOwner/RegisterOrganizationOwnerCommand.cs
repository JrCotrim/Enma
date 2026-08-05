namespace Enma.Application.Onboarding.RegisterOrganizationOwner;

public sealed class RegisterOrganizationOwnerCommand
{
    public RegisterOrganizationOwnerCommand(
        string organizationName,
        string organizationSlug,
        string ownerName,
        string ownerEmail,
        string password)
    {
        OrganizationName = organizationName;
        OrganizationSlug = organizationSlug;
        OwnerName = ownerName;
        OwnerEmail = ownerEmail;
        Password = password;
    }

    public string OrganizationName { get; }

    public string OrganizationSlug { get; }

    public string OwnerName { get; }

    public string OwnerEmail { get; }

    public string Password { get; }
}
