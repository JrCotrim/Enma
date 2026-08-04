namespace Enma.Application.Organizations.Create;

public sealed class OrganizationSlugAlreadyExistsException : InvalidOperationException
{
    public OrganizationSlugAlreadyExistsException(string slug)
        : base($"An organization with the slug '{slug}' already exists.")
    {
        Slug = slug;
    }

    public string Slug { get; }
}
