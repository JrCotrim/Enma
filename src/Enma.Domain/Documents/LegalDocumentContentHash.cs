using System.Security.Cryptography;

namespace Enma.Domain.Documents;

public sealed class LegalDocumentContentHash
    : IEquatable<LegalDocumentContentHash>
{
    private const int RequiredLength = 32;
    private readonly byte[] _value;

    public LegalDocumentContentHash(byte[] value)
    {
        if (value is null)
        {
            throw new ArgumentNullException(
                nameof(value),
                LegalDocumentErrors.ContentHashRequired);
        }

        if (value.Length != RequiredLength)
        {
            throw new ArgumentException(
                LegalDocumentErrors.ContentHashLengthInvalid,
                nameof(value));
        }

        _value = (byte[])value.Clone();
    }

    public byte[] ToArray()
    {
        return (byte[])_value.Clone();
    }

    public bool Equals(LegalDocumentContentHash? other)
    {
        return other is not null
            && CryptographicOperations.FixedTimeEquals(
                _value,
                other._value);
    }

    public override bool Equals(object? obj)
    {
        return obj is LegalDocumentContentHash other
            && Equals(other);
    }

    public override int GetHashCode()
    {
        var hashCode = new HashCode();

        foreach (byte value in _value)
        {
            hashCode.Add(value);
        }

        return hashCode.ToHashCode();
    }
}
