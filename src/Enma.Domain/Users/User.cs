namespace Enma.Domain.Users;

public sealed class User
{
    private const int MaximumNameLength = 150;
    private const int MaximumEmailLength = 254;

    public User(string name, string email, DateTimeOffset createdAt)
    {
        if (createdAt == DateTimeOffset.MinValue)
        {
            throw new ArgumentOutOfRangeException(nameof(createdAt), UserErrors.CreatedAtInvalid);
        }

        Id = Guid.NewGuid();
        Name = NormalizeName(name);
        Email = NormalizeEmail(email);
        IsActive = true;
        CreatedAt = createdAt;
        EmailVerifiedAt = null;
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; }

    public string Email { get; private set; }

    public DateTimeOffset? EmailVerifiedAt { get; private set; }

    public bool IsActive { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public void Rename(string name)
    {
        Name = NormalizeName(name);
    }

    public void ChangeEmail(string email)
    {
        string normalizedEmail = NormalizeEmail(email);

        if (string.Equals(normalizedEmail, Email, StringComparison.Ordinal))
        {
            return;
        }

        Email = normalizedEmail;
        EmailVerifiedAt = null;
    }

    public void VerifyEmail(DateTimeOffset verifiedAt)
    {
        if (verifiedAt < CreatedAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(verifiedAt),
                UserErrors.EmailVerifiedAtInvalid);
        }

        EmailVerifiedAt ??= verifiedAt;
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
            throw new ArgumentException(UserErrors.NameRequired, nameof(name));
        }

        string normalizedName = name.Trim();

        if (normalizedName.Length > MaximumNameLength)
        {
            throw new ArgumentException(UserErrors.NameTooLong, nameof(name));
        }

        return normalizedName;
    }

    public static string NormalizeEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            throw new ArgumentException(UserErrors.EmailRequired, nameof(email));
        }

        string normalizedEmail = email.Trim().ToLowerInvariant();

        if (normalizedEmail.Length > MaximumEmailLength)
        {
            throw new ArgumentException(UserErrors.EmailTooLong, nameof(email));
        }

        int atSignIndex = normalizedEmail.IndexOf('@');
        bool hasExactlyOneAtSign = atSignIndex == normalizedEmail.LastIndexOf('@');
        bool hasLocalPart = atSignIndex > 0;
        bool hasDomainPart = atSignIndex < normalizedEmail.Length - 1;
        bool containsWhitespace = normalizedEmail.Any(char.IsWhiteSpace);

        if (!hasExactlyOneAtSign || !hasLocalPart || !hasDomainPart || containsWhitespace)
        {
            throw new ArgumentException(UserErrors.EmailInvalidFormat, nameof(email));
        }

        return normalizedEmail;
    }
}
