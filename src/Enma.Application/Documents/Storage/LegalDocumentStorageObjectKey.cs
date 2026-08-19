using System.Security.Cryptography;

namespace Enma.Application.Documents.Storage;

public sealed class LegalDocumentStorageObjectKey : IEquatable<LegalDocumentStorageObjectKey>
{
    public const int ByteLength = 16;
    public const int EncodedLength = ByteLength * 2;

    private LegalDocumentStorageObjectKey(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static LegalDocumentStorageObjectKey CreateNew()
    {
        Span<byte> bytes = stackalloc byte[ByteLength];
        RandomNumberGenerator.Fill(bytes);

        return new LegalDocumentStorageObjectKey(
            Convert.ToHexString(bytes).ToLowerInvariant());
    }

    public static LegalDocumentStorageObjectKey Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        if (!IsValid(value))
        {
            throw new ArgumentException(
                "The document storage object key has an invalid format.",
                nameof(value));
        }

        return new LegalDocumentStorageObjectKey(value);
    }

    public static bool TryParse(
        string? value,
        out LegalDocumentStorageObjectKey? objectKey)
    {
        if (!IsValid(value))
        {
            objectKey = null;
            return false;
        }

        objectKey = new LegalDocumentStorageObjectKey(value!);
        return true;
    }

    public bool Equals(LegalDocumentStorageObjectKey? other)
    {
        return other is not null
            && StringComparer.Ordinal.Equals(Value, other.Value);
    }

    public override bool Equals(object? obj)
    {
        return obj is LegalDocumentStorageObjectKey other && Equals(other);
    }

    public override int GetHashCode()
    {
        return StringComparer.Ordinal.GetHashCode(Value);
    }

    public override string ToString()
    {
        return Value;
    }

    private static bool IsValid(string? value)
    {
        if (value is null || value.Length != EncodedLength)
        {
            return false;
        }

        foreach (char character in value)
        {
            bool isDigit = character is >= '0' and <= '9';
            bool isLowerHexLetter = character is >= 'a' and <= 'f';

            if (!isDigit && !isLowerHexLetter)
            {
                return false;
            }
        }

        return true;
    }
}
