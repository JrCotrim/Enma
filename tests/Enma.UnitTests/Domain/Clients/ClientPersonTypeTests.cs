using Enma.Domain.Clients;

namespace Enma.UnitTests.Domain.Clients;

public sealed class ClientPersonTypeTests
{
    private const string ValidCpf = "12345678909";
    private const string ValidCnpj = "11222333000181";
    private const string ValidAlphanumericCnpj = "12ABC34501DE35";

    private static readonly Guid OrganizationId = Guid.Parse(
        "0c2c51d6-3c5e-4f0e-9a61-8a7c1b0e5d42");

    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        10,
        2,
        12,
        0,
        0,
        TimeSpan.Zero);

    [Fact]
    public void Constructor_WithExistingCallerShape_CreatesIndividualWithoutNewFields()
    {
        var client = new Client(
            OrganizationId,
            "Maria Silva",
            CreatedAt,
            "maria@example.com",
            "(22) 98888-7777",
            "123.456.789-09");

        Assert.Equal(PersonType.Individual, client.PersonType);
        Assert.Equal(ValidCpf, client.Cpf);
        Assert.Null(client.Cnpj);
        Assert.Null(client.Address);
        Assert.Null(client.Notes);
    }

    [Fact]
    public void Constructor_WithCompanyAndFormattedCnpj_NormalizesCnpj()
    {
        var client = new Client(
            OrganizationId,
            "Acme Comercio Ltda",
            CreatedAt,
            personType: PersonType.Company,
            cnpj: " 12.abc.345/01de-35 ");

        Assert.Equal(PersonType.Company, client.PersonType);
        Assert.Equal(ValidAlphanumericCnpj, client.Cnpj);
        Assert.Null(client.Cpf);
    }

    [Fact]
    public void Constructor_WithCompanyWithoutDocument_AcceptsMissingCnpj()
    {
        var client = new Client(
            OrganizationId,
            "Acme Comercio Ltda",
            CreatedAt,
            personType: PersonType.Company,
            cnpj: "   ");

        Assert.Equal(PersonType.Company, client.PersonType);
        Assert.Null(client.Cnpj);
        Assert.Null(client.Cpf);
    }

    [Fact]
    public void Constructor_WithIndividualAndCnpj_RejectsCnpj()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => new Client(
                OrganizationId,
                "Maria Silva",
                CreatedAt,
                personType: PersonType.Individual,
                cnpj: ValidCnpj));

        Assert.Equal("cnpj", exception.ParamName);
        Assert.Contains(ClientErrors.CnpjNotAllowedForIndividual, exception.Message);
        Assert.DoesNotContain(ValidCnpj, exception.Message);
    }

    [Fact]
    public void Constructor_WithCompanyAndCpf_RejectsCpf()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => new Client(
                OrganizationId,
                "Acme Comercio Ltda",
                CreatedAt,
                cpf: ValidCpf,
                personType: PersonType.Company));

        Assert.Equal("cpf", exception.ParamName);
        Assert.Contains(ClientErrors.CpfNotAllowedForCompany, exception.Message);
        Assert.DoesNotContain(ValidCpf, exception.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(-1)]
    public void Constructor_WithUndefinedPersonType_ThrowsArgumentOutOfRangeException(
        int personType)
    {
        ArgumentOutOfRangeException exception =
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new Client(
                    OrganizationId,
                    "Maria Silva",
                    CreatedAt,
                    personType: (PersonType)personType));

        Assert.Equal("personType", exception.ParamName);
        Assert.Contains(ClientErrors.PersonTypeInvalid, exception.Message);
    }

    [Theory]
    [InlineData("11.222.333/0001-82")]
    [InlineData("00000000000000")]
    [InlineData("12ABC34501DE3!")]
    public void Constructor_WithInvalidCnpj_ThrowsArgumentException(string cnpj)
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => new Client(
                OrganizationId,
                "Acme Comercio Ltda",
                CreatedAt,
                personType: PersonType.Company,
                cnpj: cnpj));

        Assert.Equal("cnpj", exception.ParamName);
        Assert.Contains(ClientErrors.CnpjInvalid, exception.Message);
        Assert.DoesNotContain(cnpj, exception.Message);
    }

    [Fact]
    public void Constructor_WithAddressAndNotes_TrimsValues()
    {
        var client = new Client(
            OrganizationId,
            "Maria Silva",
            CreatedAt,
            address: "  Rua das Flores, 100 - Centro  ",
            notes: "\n  Prefers contact by email.\t ");

        Assert.Equal("Rua das Flores, 100 - Centro", client.Address);
        Assert.Equal("Prefers contact by email.", client.Notes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\n ")]
    public void Constructor_WithBlankAddressAndNotes_StoresNull(string? value)
    {
        var client = new Client(
            OrganizationId,
            "Maria Silva",
            CreatedAt,
            address: value,
            notes: value);

        Assert.Null(client.Address);
        Assert.Null(client.Notes);
    }

    [Fact]
    public void Constructor_WithAddressAndNotesAtMaximumLength_AcceptsValues()
    {
        string address = new('a', 300);
        string notes = new('n', 2_000);

        var client = new Client(
            OrganizationId,
            "Maria Silva",
            CreatedAt,
            address: $"  {address}  ",
            notes: $"  {notes}  ");

        Assert.Equal(address, client.Address);
        Assert.Equal(notes, client.Notes);
    }

    [Fact]
    public void Constructor_WithAddressBeyondMaximumLength_ThrowsArgumentOutOfRangeException()
    {
        ArgumentOutOfRangeException exception =
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new Client(
                    OrganizationId,
                    "Maria Silva",
                    CreatedAt,
                    address: new string('a', 301)));

        Assert.Equal("address", exception.ParamName);
        Assert.Contains(ClientErrors.AddressTooLong, exception.Message);
    }

    [Fact]
    public void Constructor_WithNotesBeyondMaximumLength_ThrowsArgumentOutOfRangeException()
    {
        ArgumentOutOfRangeException exception =
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new Client(
                    OrganizationId,
                    "Maria Silva",
                    CreatedAt,
                    notes: new string('n', 2_001)));

        Assert.Equal("notes", exception.ParamName);
        Assert.Contains(ClientErrors.NotesTooLong, exception.Message);
    }

    [Fact]
    public void UpdateProfile_WithCompanyProfile_ChangesPersonTypeAndDocument()
    {
        Client client = CreateIndividual();

        client.UpdateProfile(
            "Acme Comercio Ltda",
            "contato@acme.example",
            null,
            null,
            PersonType.Company,
            "11.222.333/0001-81",
            " Av. Central, 1 ",
            " Key account ");

        Assert.Equal("Acme Comercio Ltda", client.Name);
        Assert.Equal(PersonType.Company, client.PersonType);
        Assert.Null(client.Cpf);
        Assert.Equal(ValidCnpj, client.Cnpj);
        Assert.Equal("Av. Central, 1", client.Address);
        Assert.Equal("Key account", client.Notes);
    }

    [Fact]
    public void UpdateProfile_WithCompanyAndRemainingCpf_IsRejectedAtomically()
    {
        Client client = CreateIndividual();

        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => client.UpdateProfile(
                "Acme Comercio Ltda",
                "contato@acme.example",
                null,
                ValidCpf,
                PersonType.Company,
                ValidCnpj,
                "Av. Central, 1",
                "Key account"));

        Assert.Equal("cpf", exception.ParamName);
        AssertOriginalIndividual(client);
    }

    [Fact]
    public void UpdateProfile_WithNotesBeyondMaximumLength_IsRejectedAtomically()
    {
        Client client = CreateIndividual();

        ArgumentOutOfRangeException exception =
            Assert.Throws<ArgumentOutOfRangeException>(
                () => client.UpdateProfile(
                    "Changed Name",
                    "changed@example.com",
                    null,
                    null,
                    PersonType.Individual,
                    null,
                    "Changed address",
                    new string('n', 2_001)));

        Assert.Equal("notes", exception.ParamName);
        AssertOriginalIndividual(client);
    }

    [Fact]
    public void UpdateProfile_ExistingOverload_PreservesPersonTypeAddressAndNotes()
    {
        var client = new Client(
            OrganizationId,
            "Acme Comercio Ltda",
            CreatedAt,
            personType: PersonType.Company,
            cnpj: ValidCnpj,
            address: "Av. Central, 1",
            notes: "Key account");

        client.UpdateProfile(
            "Acme Renamed Ltda",
            "contato@acme.example",
            null,
            null);

        Assert.Equal("Acme Renamed Ltda", client.Name);
        Assert.Equal("contato@acme.example", client.Email);
        Assert.Equal(PersonType.Company, client.PersonType);
        Assert.Equal(ValidCnpj, client.Cnpj);
        Assert.Equal("Av. Central, 1", client.Address);
        Assert.Equal("Key account", client.Notes);
    }

    [Fact]
    public void UpdateProfile_ExistingOverloadWithCpfOnCompany_IsRejectedAtomically()
    {
        var client = new Client(
            OrganizationId,
            "Acme Comercio Ltda",
            CreatedAt,
            personType: PersonType.Company,
            cnpj: ValidCnpj);

        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => client.UpdateProfile(
                "Acme Renamed Ltda",
                null,
                null,
                ValidCpf));

        Assert.Equal("cpf", exception.ParamName);
        Assert.Equal("Acme Comercio Ltda", client.Name);
        Assert.Equal(PersonType.Company, client.PersonType);
        Assert.Null(client.Cpf);
        Assert.Equal(ValidCnpj, client.Cnpj);
    }

    private static Client CreateIndividual()
    {
        return new Client(
            OrganizationId,
            "Maria Silva",
            CreatedAt,
            "maria@example.com",
            "(22) 98888-7777",
            ValidCpf,
            address: "Rua das Flores, 100",
            notes: "Original notes");
    }

    private static void AssertOriginalIndividual(Client client)
    {
        Assert.Equal("Maria Silva", client.Name);
        Assert.Equal("maria@example.com", client.Email);
        Assert.Equal("22988887777", client.Phone);
        Assert.Equal(PersonType.Individual, client.PersonType);
        Assert.Equal(ValidCpf, client.Cpf);
        Assert.Null(client.Cnpj);
        Assert.Equal("Rua das Flores, 100", client.Address);
        Assert.Equal("Original notes", client.Notes);
    }
}
