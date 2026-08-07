using System.Security.Cryptography;

namespace Enma.Domain.Authentication;

public sealed class AuthenticationSessionSecretHash
    : IEquatable<AuthenticationSessionSecretHash>
{
    private const int RequiredLength = 32;
    private readonly byte[] _value;

    public AuthenticationSessionSecretHash(byte[] value)
    {
        if (value is null)
        {
            throw new ArgumentNullException(
                nameof(value),
                AuthenticationSessionErrors.SecretHashRequired);
        }

        if (value.Length != RequiredLength)
        {
            throw new ArgumentException(
                AuthenticationSessionErrors.SecretHashLengthInvalid,
                nameof(value));
        }

        _value = (byte[])value.Clone();
    }

    public byte[] ToArray()
    {
        return (byte[])_value.Clone();
    }

    public bool Equals(AuthenticationSessionSecretHash? other)
    {
        return other is not null &&
            CryptographicOperations.FixedTimeEquals(_value, other._value);
    }

    public override bool Equals(object? obj)
    {
        return obj is AuthenticationSessionSecretHash other && Equals(other);
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
