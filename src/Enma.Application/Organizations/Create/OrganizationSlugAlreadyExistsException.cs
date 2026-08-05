namespace Enma.Application.Organizations.Create;

public sealed class OrganizationSlugAlreadyExistsException : InvalidOperationException
{
    public OrganizationSlugAlreadyExistsException(string slug)
        : this(slug, null)
    {
    }

    public OrganizationSlugAlreadyExistsException(
        string slug,
        Exception? innerException)
        : base($"An organization with the slug '{slug}' already exists.", innerException)
    {
        Slug = slug;
    }

    public string Slug { get; }
}
