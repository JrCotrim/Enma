using Enma.Domain.Organizations;

namespace Enma.UnitTests.Domain.Organizations;

public sealed class OrganizationTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Constructor_WithValidValues_CreatesOrganization()
    {
        var organization = new Organization("Enma Advocacia", "enma-advocacia", CreatedAt);

        Assert.Equal("Enma Advocacia", organization.Name);
        Assert.Equal("enma-advocacia", organization.Slug);
        Assert.Equal(CreatedAt, organization.CreatedAt);
    }

    [Fact]
    public void Constructor_TrimsName()
    {
        var organization = new Organization("  Enma Advocacia  ", "enma-advocacia", CreatedAt);

        Assert.Equal("Enma Advocacia", organization.Name);
    }

    [Fact]
    public void Constructor_TrimsAndLowercasesSlug()
    {
        var organization = new Organization("Enma Advocacia", "  ENMA-ADVOCACIA  ", CreatedAt);

        Assert.Equal("enma-advocacia", organization.Slug);
    }

    [Fact]
    public void Constructor_CreatesActiveOrganization()
    {
        var organization = CreateOrganization();

        Assert.True(organization.IsActive);
    }

    [Fact]
    public void Constructor_GeneratesNonEmptyId()
    {
        var organization = CreateOrganization();

        Assert.NotEqual(Guid.Empty, organization.Id);
    }

    [Fact]
    public void Constructor_WithEmptyName_ThrowsArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new Organization("   ", "enma-advocacia", CreatedAt));

        Assert.Contains(OrganizationErrors.NameRequired, exception.Message);
    }

    [Fact]
    public void Constructor_WithNullName_ThrowsArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new Organization(null!, "enma-advocacia", CreatedAt));

        Assert.Contains(OrganizationErrors.NameRequired, exception.Message);
    }

    [Fact]
    public void Constructor_WithNameContainingExactly150Characters_AcceptsName()
    {
        string name = new('a', 150);

        var organization = new Organization(name, "enma-advocacia", CreatedAt);

        Assert.Equal(name, organization.Name);
    }

    [Fact]
    public void Constructor_WithNameLongerThan150Characters_ThrowsArgumentOutOfRangeException()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new Organization(new string('a', 151), "enma-advocacia", CreatedAt));

        Assert.Contains(OrganizationErrors.NameTooLong, exception.Message);
    }

    [Fact]
    public void Constructor_WithEmptySlug_ThrowsArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new Organization("Enma Advocacia", "   ", CreatedAt));

        Assert.Contains(OrganizationErrors.SlugRequired, exception.Message);
    }

    [Fact]
    public void Constructor_WithNullSlug_ThrowsArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new Organization("Enma Advocacia", null!, CreatedAt));

        Assert.Contains(OrganizationErrors.SlugRequired, exception.Message);
    }

    [Fact]
    public void Constructor_WithSlugContainingExactly80Characters_AcceptsSlug()
    {
        string slug = new('a', 80);

        var organization = new Organization("Enma Advocacia", slug, CreatedAt);

        Assert.Equal(slug, organization.Slug);
    }

    [Fact]
    public void Constructor_WithSlugLongerThan80Characters_ThrowsArgumentOutOfRangeException()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new Organization("Enma Advocacia", new string('a', 81), CreatedAt));

        Assert.Contains(OrganizationErrors.SlugTooLong, exception.Message);
    }

    [Fact]
    public void Constructor_WithInvalidSlugCharacters_ThrowsArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new Organization("Enma Advocacia", "enma_advocacia", CreatedAt));

        Assert.Contains(OrganizationErrors.SlugInvalidFormat, exception.Message);
    }

    [Fact]
    public void Constructor_WithSlugStartingWithHyphen_ThrowsArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new Organization("Enma Advocacia", "-enma", CreatedAt));

        Assert.Contains(OrganizationErrors.SlugInvalidFormat, exception.Message);
    }

    [Fact]
    public void Constructor_WithSlugEndingWithHyphen_ThrowsArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new Organization("Enma Advocacia", "enma-", CreatedAt));

        Assert.Contains(OrganizationErrors.SlugInvalidFormat, exception.Message);
    }

    [Fact]
    public void Constructor_WithConsecutiveHyphensInSlug_ThrowsArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new Organization("Enma Advocacia", "enma--advocacia", CreatedAt));

        Assert.Contains(OrganizationErrors.SlugInvalidFormat, exception.Message);
    }

    [Fact]
    public void Constructor_WithSlugContainingOnlyHyphen_ThrowsArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new Organization("Enma Advocacia", "-", CreatedAt));

        Assert.Contains(OrganizationErrors.SlugInvalidFormat, exception.Message);
    }

    [Fact]
    public void Constructor_WithMinimumCreatedAt_ThrowsArgumentOutOfRangeException()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new Organization("Enma Advocacia", "enma-advocacia", DateTimeOffset.MinValue));

        Assert.Contains(OrganizationErrors.CreatedAtInvalid, exception.Message);
    }

    [Fact]
    public void Rename_WithValidName_ChangesAndNormalizesName()
    {
        var organization = CreateOrganization();

        organization.Rename("  Novo Nome  ");

        Assert.Equal("Novo Nome", organization.Name);
    }

    [Fact]
    public void Rename_WithInvalidName_ThrowsArgumentExceptionAndPreservesName()
    {
        var organization = CreateOrganization();

        var exception = Assert.Throws<ArgumentException>(() => organization.Rename("   "));

        Assert.Contains(OrganizationErrors.NameRequired, exception.Message);
        Assert.Equal("Enma Advocacia", organization.Name);
    }

    [Fact]
    public void Activate_ChangesInactiveOrganizationToActive()
    {
        var organization = CreateOrganization();
        organization.Deactivate();

        organization.Activate();

        Assert.True(organization.IsActive);
    }

    [Fact]
    public void Deactivate_ChangesActiveOrganizationToInactive()
    {
        var organization = CreateOrganization();

        organization.Deactivate();

        Assert.False(organization.IsActive);
    }

    [Fact]
    public void Activate_WhenAlreadyActive_RemainsActive()
    {
        var organization = CreateOrganization();

        organization.Activate();
        organization.Activate();

        Assert.True(organization.IsActive);
    }

    [Fact]
    public void Deactivate_WhenAlreadyInactive_RemainsInactive()
    {
        var organization = CreateOrganization();
        organization.Deactivate();

        organization.Deactivate();

        Assert.False(organization.IsActive);
    }

    private static Organization CreateOrganization()
    {
        return new Organization("Enma Advocacia", "enma-advocacia", CreatedAt);
    }
}
