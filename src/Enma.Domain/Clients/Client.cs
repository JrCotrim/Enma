using System.Net.Mail;

namespace Enma.Domain.Clients;

public sealed class Client
{
    private const int MaximumNameLength = 150;
    private const int MaximumEmailLength = 254;
    private const int MinimumPhoneLength = 8;
    private const int MaximumPhoneLength = 15;
    private const int MaximumAddressLength = 300;
    private const int MaximumNotesLength = 2_000;

    public Client(
        Guid organizationId,
        string name,
        DateTimeOffset createdAt,
        string? email = null,
        string? phone = null,
        string? cpf = null,
        PersonType personType = PersonType.Individual,
        string? cnpj = null,
        string? address = null,
        string? notes = null)
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

        string normalizedName = NormalizeName(name);
        string? normalizedEmail = NormalizeEmail(email);
        string? normalizedPhone = NormalizePhone(phone);
        string? normalizedCpf = NormalizeCpf(cpf);
        PersonType validatedPersonType = ValidatePersonType(personType);
        string? normalizedCnpj = NormalizeCnpj(cnpj);
        string? normalizedAddress = NormalizeAddress(address);
        string? normalizedNotes = NormalizeNotes(notes);

        ValidateDocumentMatchesPersonType(
            validatedPersonType,
            normalizedCpf,
            normalizedCnpj);

        Id = Guid.NewGuid();
        OrganizationId = organizationId;
        Name = normalizedName;
        Email = normalizedEmail;
        Phone = normalizedPhone;
        Cpf = normalizedCpf;
        PersonType = validatedPersonType;
        Cnpj = normalizedCnpj;
        Address = normalizedAddress;
        Notes = normalizedNotes;
        IsActive = true;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public string Name { get; private set; }

    public string? Email { get; private set; }

    public string? Phone { get; private set; }

    public string? Cpf { get; private set; }

    public PersonType PersonType { get; private set; }

    public string? Cnpj { get; private set; }

    public string? Address { get; private set; }

    public string? Notes { get; private set; }

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
        UpdateProfile(
            name,
            email,
            phone,
            cpf,
            PersonType,
            Cnpj,
            Address,
            Notes);
    }

    public void UpdateProfile(
        string name,
        string? email,
        string? phone,
        string? cpf,
        PersonType personType,
        string? cnpj,
        string? address,
        string? notes)
    {
        string normalizedName = NormalizeName(name);
        string? normalizedEmail = NormalizeEmail(email);
        string? normalizedPhone = NormalizePhone(phone);
        string? normalizedCpf = NormalizeCpf(cpf);
        PersonType validatedPersonType = ValidatePersonType(personType);
        string? normalizedCnpj = NormalizeCnpj(cnpj);
        string? normalizedAddress = NormalizeAddress(address);
        string? normalizedNotes = NormalizeNotes(notes);

        ValidateDocumentMatchesPersonType(
            validatedPersonType,
            normalizedCpf,
            normalizedCnpj);

        Name = normalizedName;
        Email = normalizedEmail;
        Phone = normalizedPhone;
        Cpf = normalizedCpf;
        PersonType = validatedPersonType;
        Cnpj = normalizedCnpj;
        Address = normalizedAddress;
        Notes = normalizedNotes;
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

        if (!BrazilianTaxId.TryNormalizeCpf(cpf, out string? normalizedCpf))
        {
            throw new ArgumentException(
                ClientErrors.CpfInvalid,
                nameof(cpf));
        }

        return normalizedCpf;
    }

    private static PersonType ValidatePersonType(PersonType personType)
    {
        if (!Enum.IsDefined(personType))
        {
            throw new ArgumentOutOfRangeException(
                nameof(personType),
                ClientErrors.PersonTypeInvalid);
        }

        return personType;
    }

    private static string? NormalizeCnpj(string? cnpj)
    {
        if (string.IsNullOrWhiteSpace(cnpj))
        {
            return null;
        }

        if (!BrazilianTaxId.TryNormalizeCnpj(cnpj, out string? normalizedCnpj))
        {
            throw new ArgumentException(
                ClientErrors.CnpjInvalid,
                nameof(cnpj));
        }

        return normalizedCnpj;
    }

    private static string? NormalizeAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        string normalizedAddress = address.Trim();

        if (normalizedAddress.Length > MaximumAddressLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(address),
                ClientErrors.AddressTooLong);
        }

        return normalizedAddress;
    }

    private static string? NormalizeNotes(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes))
        {
            return null;
        }

        string normalizedNotes = notes.Trim();

        if (normalizedNotes.Length > MaximumNotesLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(notes),
                ClientErrors.NotesTooLong);
        }

        return normalizedNotes;
    }

    private static void ValidateDocumentMatchesPersonType(
        PersonType personType,
        string? cpf,
        string? cnpj)
    {
        if (personType == PersonType.Individual && cnpj is not null)
        {
            throw new ArgumentException(
                ClientErrors.CnpjNotAllowedForIndividual,
                nameof(cnpj));
        }

        if (personType == PersonType.Company && cpf is not null)
        {
            throw new ArgumentException(
                ClientErrors.CpfNotAllowedForCompany,
                nameof(cpf));
        }
    }
}