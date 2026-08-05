using Enma.Domain.Organizations;
using Enma.Infrastructure.Persistence;
using Enma.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Enma.IntegrationTests.Infrastructure.Persistence;

[Collection(PostgreSqlCollection.Name)]
public sealed class OrganizationPersistenceTests(PostgreSqlFixture fixture)
{
    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        1,
        2,
        3,
        4,
        5,
        TimeSpan.Zero);

    [Fact]
    public async Task AddAsync_WithValidOrganization_PersistsOrganization()
    {
        await using EnmaDbContext dbContext = await CreateEmptyDbContextAsync();
        OrganizationRepository repository = new(dbContext);
        Organization organization = new("Enma Legal", "enma-legal", CreatedAt);

        await repository.AddAsync(organization);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        Organization persistedOrganization = await dbContext.Organizations.SingleAsync();

        Assert.Equal(organization.Id, persistedOrganization.Id);
        Assert.Equal("Enma Legal", persistedOrganization.Name);
        Assert.Equal("enma-legal", persistedOrganization.Slug);
        Assert.True(persistedOrganization.IsActive);
        Assert.Equal(CreatedAt, persistedOrganization.CreatedAt);
    }

    [Fact]
    public async Task ExistsBySlugAsync_WithExistingSlug_ReturnsTrue()
    {
        await using EnmaDbContext dbContext = await CreateEmptyDbContextAsync();
        OrganizationRepository repository = new(dbContext);
        Organization organization = new("Enma Legal", "enma-legal", CreatedAt);
        await repository.AddAsync(organization);
        await dbContext.SaveChangesAsync();

        bool exists = await repository.ExistsBySlugAsync("enma-legal");

        Assert.True(exists);
    }

    [Fact]
    public async Task ExistsBySlugAsync_WithMissingSlug_ReturnsFalse()
    {
        await using EnmaDbContext dbContext = await CreateEmptyDbContextAsync();
        OrganizationRepository repository = new(dbContext);

        bool exists = await repository.ExistsBySlugAsync("missing-organization");

        Assert.False(exists);
    }

    [Fact]
    public async Task SaveChangesAsync_WithDuplicateSlug_ThrowsDbUpdateException()
    {
        await using EnmaDbContext dbContext = await CreateEmptyDbContextAsync();
        OrganizationRepository repository = new(dbContext);
        Organization firstOrganization = new("First Legal", "shared-slug", CreatedAt);
        Organization secondOrganization = new("Second Legal", "shared-slug", CreatedAt.AddMinutes(1));
        await repository.AddAsync(firstOrganization);
        await repository.AddAsync(secondOrganization);

        await Assert.ThrowsAsync<DbUpdateException>(() => dbContext.SaveChangesAsync());
    }

    [Fact]
    public async Task Query_AfterOrganizationIsDeactivated_MaterializesPersistedState()
    {
        await using EnmaDbContext dbContext = await CreateEmptyDbContextAsync();
        Organization organization = new("Enma Legal", "enma-legal", CreatedAt);
        dbContext.Organizations.Add(organization);
        await dbContext.SaveChangesAsync();

        organization.Deactivate();
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        Organization persistedOrganization = await dbContext.Organizations.SingleAsync();

        Assert.Equal(organization.Id, persistedOrganization.Id);
        Assert.Equal("Enma Legal", persistedOrganization.Name);
        Assert.Equal("enma-legal", persistedOrganization.Slug);
        Assert.False(persistedOrganization.IsActive);
        Assert.Equal(CreatedAt, persistedOrganization.CreatedAt);
    }

    private async Task<EnmaDbContext> CreateEmptyDbContextAsync()
    {
        EnmaDbContext dbContext = fixture.CreateDbContext();
        await dbContext.Organizations.ExecuteDeleteAsync();
        return dbContext;
    }
}
