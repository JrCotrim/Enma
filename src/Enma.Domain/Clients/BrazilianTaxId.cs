using System.Diagnostics.CodeAnalysis;

namespace Enma.Domain.Clients;

public static class BrazilianTaxId
{
    public const int CpfLength = 11;
    public const int CnpjLength = 14;

    private const int CnpjBaseLength = 12;

    public static bool TryNormalizeCpf(
        string value,
        [NotNullWhen(true)] out string? normalizedCpf)
    {
        ArgumentNullException.ThrowIfNull(value);

        normalizedCpf = null;
        string trimmedCpf = value.Trim();

        foreach (char character in trimmedCpf)
        {
            bool allowed =
                character is >= '0' and <= '9' ||
                character is '.' or '-' or ' ';

            if (!allowed)
            {
                return false;
            }
        }

        string digits = new(
            trimmedCpf
                .Where(character => character is >= '0' and <= '9')
                .ToArray());

        if (digits.Length != CpfLength ||
            !HasValidCpfCheckDigits(digits))
        {
            return false;
        }

        normalizedCpf = digits;
        return true;
    }

    public static bool TryNormalizeCnpj(
        string value,
        [NotNullWhen(true)] out string? normalizedCnpj)
    {
        ArgumentNullException.ThrowIfNull(value);

        normalizedCnpj = null;
        string trimmedCnpj = value.Trim();

        // Only ASCII letters are accepted, so culture-specific case mappings
        // (for example U+0131 to 'I') can never turn an invalid input valid.
        foreach (char character in trimmedCnpj)
        {
            bool allowed =
                character is >= '0' and <= '9' ||
                character is >= 'A' and <= 'Z' ||
                character is >= 'a' and <= 'z' ||
                character is '.' or '/' or '-' or ' ';

            if (!allowed)
            {
                return false;
            }
        }

        string characters = new(
            trimmedCnpj
                .Where(character => character is not ('.' or '/' or '-' or ' '))
                .Select(character => character is >= 'a' and <= 'z'
                    ? (char)(character - 'a' + 'A')
                    : character)
                .ToArray());

        if (characters.Length != CnpjLength ||
            !HasValidCnpjFormat(characters) ||
            !HasValidCnpjCheckDigits(characters))
        {
            return false;
        }

        normalizedCnpj = characters;
        return true;
    }

    private static bool HasValidCpfCheckDigits(string cpf)
    {
        if (HasAllCharactersEqual(cpf))
        {
            return false;
        }

        int firstSum = 0;

        for (int index = 0; index < 9; index++)
        {
            firstSum += (cpf[index] - '0') * (10 - index);
        }

        int firstDigit = 11 - firstSum % 11;

        if (firstDigit >= 10)
        {
            firstDigit = 0;
        }

        if (cpf[9] - '0' != firstDigit)
        {
            return false;
        }

        int secondSum = 0;

        for (int index = 0; index < 10; index++)
        {
            secondSum += (cpf[index] - '0') * (11 - index);
        }

        int secondDigit = 11 - secondSum % 11;

        if (secondDigit >= 10)
        {
            secondDigit = 0;
        }

        return cpf[10] - '0' == secondDigit;
    }

    private static bool HasValidCnpjFormat(string cnpj)
    {
        for (int index = 0; index < CnpjBaseLength; index++)
        {
            char character = cnpj[index];

            if (character is not (>= '0' and <= '9' or >= 'A' and <= 'Z'))
            {
                return false;
            }
        }

        return cnpj[CnpjBaseLength] is >= '0' and <= '9' &&
            cnpj[CnpjBaseLength + 1] is >= '0' and <= '9';
    }

    private static bool HasValidCnpjCheckDigits(string cnpj)
    {
        if (HasAllCharactersEqual(cnpj))
        {
            return false;
        }

        int firstDigit = CalculateCnpjCheckDigit(cnpj.AsSpan(0, CnpjBaseLength));

        if (cnpj[CnpjBaseLength] - '0' != firstDigit)
        {
            return false;
        }

        int secondDigit = CalculateCnpjCheckDigit(
            cnpj.AsSpan(0, CnpjBaseLength + 1));

        return cnpj[CnpjBaseLength + 1] - '0' == secondDigit;
    }

    // Each character is valued as its ASCII code minus 48 ('0' = 0, 'A' = 17),
    // weighted from 2 to 9 right to left, cycling, under modulo 11.
    private static int CalculateCnpjCheckDigit(ReadOnlySpan<char> characters)
    {
        int sum = 0;
        int weight = 2;

        for (int index = characters.Length - 1; index >= 0; index--)
        {
            sum += (characters[index] - '0') * weight;
            weight = weight == 9 ? 2 : weight + 1;
        }

        int remainder = sum % 11;

        return remainder < 2 ? 0 : 11 - remainder;
    }

    private static bool HasAllCharactersEqual(string value)
    {
        for (int index = 1; index < value.Length; index++)
        {
            if (value[index] != value[0])
            {
                return false;
            }
        }

        return true;
    }
}
