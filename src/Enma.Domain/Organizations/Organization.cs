namespace Enma.Domain.Organizations;

public sealed class Organization
{
    private const int MaximumNameLength = 150;
    private const int MaximumSlugLength = 80;

    public Organization(string name, string slug, DateTimeOffset createdAt)
    {
        if (createdAt == DateTimeOffset.MinValue)
        {
            throw new ArgumentOutOfRangeException(nameof(createdAt), OrganizationErrors.CreatedAtInvalid);
        }

        Id = Guid.NewGuid();
        Name = NormalizeName(name);
        Slug = NormalizeSlug(slug);
        IsActive = true;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; }

    public string Slug { get; private set; }

    public bool IsActive { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public void Rename(string name)
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
            throw new ArgumentException(OrganizationErrors.NameRequired, nameof(name));
        }

        string normalizedName = name.Trim();

        if (normalizedName.Length > MaximumNameLength)
        {
            throw new ArgumentOutOfRangeException(nameof(name), OrganizationErrors.NameTooLong);
        }

        return normalizedName;
    }

    private static string NormalizeSlug(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
        {
            throw new ArgumentException(OrganizationErrors.SlugRequired, nameof(slug));
        }

        string normalizedSlug = slug.Trim().ToLowerInvariant();

        if (normalizedSlug.Length > MaximumSlugLength)
        {
            throw new ArgumentOutOfRangeException(nameof(slug), OrganizationErrors.SlugTooLong);
        }

        if (!HasValidSlugFormat(normalizedSlug))
        {
            throw new ArgumentException(OrganizationErrors.SlugInvalidFormat, nameof(slug));
        }

        return normalizedSlug;
    }

    private static bool HasValidSlugFormat(string slug)
    {
        for (int index = 0; index < slug.Length; index++)
        {
            char character = slug[index];
            bool isLowercaseLetter = character is >= 'a' and <= 'z';
            bool isNumber = character is >= '0' and <= '9';
            bool isHyphen = character == '-';

            if (!isLowercaseLetter && !isNumber && !isHyphen)
            {
                return false;
            }

            if (isHyphen &&
                (index == 0 || index == slug.Length - 1 || slug[index - 1] == '-'))
            {
                return false;
            }
        }

        return true;
    }
}
