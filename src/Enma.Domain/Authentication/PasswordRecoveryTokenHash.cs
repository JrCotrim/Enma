using System.Security.Cryptography;

namespace Enma.Domain.Authentication;

public sealed class PasswordRecoveryTokenHash
    : IEquatable<PasswordRecoveryTokenHash>
{
    private const int RequiredLength = 32;
    private readonly byte[] value;

    public PasswordRecoveryTokenHash(byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Length != RequiredLength)
        {
            throw new ArgumentException(
                PasswordRecoveryChallengeErrors.TokenHashLengthInvalid,
                nameof(value));
        }

        this.value = (byte[])value.Clone();
    }

    public byte[] ToArray() => (byte[])value.Clone();

    public bool Equals(PasswordRecoveryTokenHash? other) =>
        other is not null && CryptographicOperations.FixedTimeEquals(value, other.value);

    public override bool Equals(object? obj) =>
        obj is PasswordRecoveryTokenHash other && Equals(other);

    public override int GetHashCode()
    {
        var hashCode = new HashCode();

        foreach (byte item in value)
        {
            hashCode.Add(item);
        }

        return hashCode.ToHashCode();
    }
}
