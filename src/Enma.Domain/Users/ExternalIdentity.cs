namespace Enma.Domain.Users;

public sealed class ExternalIdentity
{
    private const int MaximumProviderLength = 50;
    private const int MaximumSubjectLength = 255;

    public ExternalIdentity(
        Guid userId,
        string provider,
        string providerSubject,
        DateTimeOffset createdAt)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("The user id is required.", nameof(userId));
        }

        if (createdAt == DateTimeOffset.MinValue)
        {
            throw new ArgumentOutOfRangeException(nameof(createdAt));
        }

        Id = Guid.NewGuid();
        UserId = userId;
        Provider = Validate(provider, MaximumProviderLength, nameof(provider));
        ProviderSubject = Validate(
            providerSubject,
            MaximumSubjectLength,
            nameof(providerSubject));
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public string Provider { get; private set; }

    public string ProviderSubject { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    private static string Validate(string value, int maximumLength, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("The value is required.", parameterName);
        }

        string normalized = value.Trim();
        if (normalized.Length > maximumLength)
        {
            throw new ArgumentException("The value is too long.", parameterName);
        }

        return normalized;
    }
}
