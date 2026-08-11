using System.Reflection;
using Enma.Domain.Clients;

namespace Enma.UnitTests.Domain.Clients;

public sealed class ClientTests
{
    private static readonly Guid OrganizationId = Guid.Parse(
        "1849b277-1183-49a8-9b78-a02313c56bed");

    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        8,
        11,
        14,
        0,
        0,
        TimeSpan.Zero);

    [Fact]
    public void Constructor_WithValidValues_CreatesClient()
    {
        var client = new Client(OrganizationId, "Acme Legal", CreatedAt);

        Assert.NotEqual(Guid.Empty, client.Id);
        Assert.Equal(OrganizationId, client.OrganizationId);
        Assert.Equal("Acme Legal", client.Name);
        Assert.True(client.IsActive);
        Assert.Equal(CreatedAt, client.CreatedAt);
    }

    [Fact]
    public void Constructor_WithSurroundingNameWhitespace_TrimsName()
    {
        var client = new Client(OrganizationId, "  Acme Legal  ", CreatedAt);

        Assert.Equal("Acme Legal", client.Name);
    }

    [Fact]
    public void Constructor_WithEmptyOrganizationId_ThrowsArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new Client(Guid.Empty, "Acme Legal", CreatedAt));

        Assert.Equal("organizationId", exception.ParamName);
        Assert.Contains(ClientErrors.OrganizationIdRequired, exception.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_WithUnusableName_ThrowsArgumentException(string name)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new Client(OrganizationId, name, CreatedAt));

        Assert.Equal("name", exception.ParamName);
        Assert.Contains(ClientErrors.NameRequired, exception.Message);
    }

    [Fact]
    public void Constructor_WithNullName_ThrowsArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new Client(OrganizationId, null!, CreatedAt));

        Assert.Equal("name", exception.ParamName);
        Assert.Contains(ClientErrors.NameRequired, exception.Message);
    }

    [Fact]
    public void Constructor_WithNameAtMaximumLength_AcceptsName()
    {
        string name = new('a', 150);

        var client = new Client(OrganizationId, name, CreatedAt);

        Assert.Equal(name, client.Name);
    }

    [Fact]
    public void Constructor_WithNameBeyondMaximumLength_ThrowsArgumentOutOfRangeException()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new Client(OrganizationId, new string('a', 151), CreatedAt));

        Assert.Equal("name", exception.ParamName);
        Assert.Contains(ClientErrors.NameTooLong, exception.Message);
    }

    [Fact]
    public void Constructor_WithMinimumCreatedAt_ThrowsArgumentOutOfRangeException()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new Client(OrganizationId, "Acme Legal", DateTimeOffset.MinValue));

        Assert.Equal("createdAt", exception.ParamName);
        Assert.Contains(ClientErrors.CreatedAtInvalid, exception.Message);
    }

    [Fact]
    public void Deactivate_WithActiveClient_MakesClientInactive()
    {
        Client client = CreateClient();

        client.Deactivate();

        Assert.False(client.IsActive);
    }

    [Fact]
    public void Activate_WithInactiveClient_MakesClientActive()
    {
        Client client = CreateClient();
        client.Deactivate();

        client.Activate();

        Assert.True(client.IsActive);
    }

    [Fact]
    public void OrganizationId_PublicContract_HasNoOwnershipTransferPath()
    {
        PropertyInfo organizationIdProperty = Assert.Single(
            typeof(Client).GetProperties(),
            property => property.Name == nameof(Client.OrganizationId));

        Assert.NotNull(organizationIdProperty.SetMethod);
        Assert.False(organizationIdProperty.SetMethod.IsPublic);
        Assert.Null(typeof(Client).GetMethod("ChangeOrganization"));
        Assert.Null(typeof(Client).GetMethod("SetOrganization"));
        Assert.Null(typeof(Client).GetMethod("TransferOrganization"));
    }

    private static Client CreateClient()
    {
        return new Client(OrganizationId, "Acme Legal", CreatedAt);
    }
}
