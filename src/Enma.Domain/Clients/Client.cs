using System.Net.Mail;

namespace Enma.Domain.Clients;

public sealed class Client
{
    private const int MaximumNameLength = 150;
    private const int MaximumEmailLength = 254;
    private const int MinimumPhoneLength = 8;
    private const int MaximumPhoneLength = 15;
    private const int CpfLength = 11;

    public Client(
        Guid organizationId,
        string name,
        DateTimeOffset createdAt,
        string? email = null,
        string? phone = null,
        string? cpf = null)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException(
                ClientErrors.OrganizationIdRequired,
                nameof(organizationId));
        }

        if (createdAt == DateTimeOffset.MinValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(createdAt),
                ClientErrors.CreatedAtInvalid);
        }

        Id = Guid.NewGuid();
        OrganizationId = organizationId;
        Name = NormalizeName(name);
        Email = NormalizeEmail(email);
        Phone = NormalizePhone(phone);
        Cpf = NormalizeCpf(cpf);
        IsActive = true;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public string Name { get; private set; }

    public string? Email { get; private set; }

    public string? Phone { get; private set; }

    public string? Cpf { get; private set; }

    public bool IsActive { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public void ChangeName(string name)
    {
        Name = NormalizeName(name);
    }

    public void UpdateProfile(
        string name,
        string? email,
        string? phone,
        string? cpf)
    {
        string normalizedName = NormalizeName(name);
        string? normalizedEmail = NormalizeEmail(email);
        string? normalizedPhone = NormalizePhone(phone);
        string? normalizedCpf = NormalizeCpf(cpf);

        Name = normalizedName;
        Email = normalizedEmail;
        Phone = normalizedPhone;
        Cpf = normalizedCpf;
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
            throw new ArgumentException(ClientErrors.NameRequired, nameof(name));
        }

        string normalizedName = name.Trim();

        if (normalizedName.Length > MaximumNameLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(name),
                ClientErrors.NameTooLong);
        }

        return normalizedName;
    }

    private static string? NormalizeEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return null;
        }

        string normalizedEmail = email.Trim().ToLowerInvariant();

        if (normalizedEmail.Length > MaximumEmailLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(email),
                ClientErrors.EmailTooLong);
        }

        if (!MailAddress.TryCreate(normalizedEmail, out MailAddress? parsed) ||
            !StringComparer.OrdinalIgnoreCase.Equals(
                parsed.Address,
                normalizedEmail))
        {
            throw new ArgumentException(
                ClientErrors.EmailInvalid,
                nameof(email));
        }

        return normalizedEmail;
    }

    private static string? NormalizePhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
        {
            return null;
        }

        string trimmedPhone = phone.Trim();

        foreach (char character in trimmedPhone)
        {
            bool allowed =
                character is >= '0' and <= '9' ||
                character is ' ' or '+' or '-' or '(' or ')' or '.';

            if (!allowed)
            {
                throw new ArgumentException(
                    ClientErrors.PhoneInvalid,
                    nameof(phone));
            }
        }

        string normalizedPhone = new(
            trimmedPhone
                .Where(character => character is >= '0' and <= '9')
                .ToArray());

        if (normalizedPhone.Length < MinimumPhoneLength ||
            normalizedPhone.Length > MaximumPhoneLength)
        {
            throw new ArgumentException(
                ClientErrors.PhoneInvalid,
                nameof(phone));
        }

        return normalizedPhone;
    }

    private static string? NormalizeCpf(string? cpf)
    {
        if (string.IsNullOrWhiteSpace(cpf))
        {
            return null;
        }

        string trimmedCpf = cpf.Trim();

        foreach (char character in trimmedCpf)
        {
            bool allowed =
                character is >= '0' and <= '9' ||
                character is '.' or '-' or ' ';

            if (!allowed)
            {
                throw new ArgumentException(
                    ClientErrors.CpfInvalid,
                    nameof(cpf));
            }
        }

        string normalizedCpf = new(
            trimmedCpf
                .Where(character => character is >= '0' and <= '9')
                .ToArray());

        if (normalizedCpf.Length != CpfLength ||
            !HasValidCpfCheckDigits(normalizedCpf))
        {
            throw new ArgumentException(
                ClientErrors.CpfInvalid,
                nameof(cpf));
        }

        return normalizedCpf;
    }

    private static bool HasValidCpfCheckDigits(string cpf)
    {
        bool allDigitsEqual = true;

        for (int index = 1; index < cpf.Length; index++)
        {
            if (cpf[index] != cpf[0])
            {
                allDigitsEqual = false;
                break;
            }
        }

        if (allDigitsEqual)
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
}