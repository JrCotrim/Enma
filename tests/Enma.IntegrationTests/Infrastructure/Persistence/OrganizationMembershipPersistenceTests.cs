using Enma.Domain.Organizations;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Npgsql;

namespace Enma.IntegrationTests.Infrastructure.Persistence;

[Collection(PostgreSqlCollection.Name)]
public sealed class OrganizationMembershipPersistenceTests(PostgreSqlFixture fixture)
    : IAsyncLifetime
{
    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        1,
        2,
        3,
        4,
        5,
        TimeSpan.Zero);

    public Task InitializeAsync()
    {
        return fixture.ResetDatabaseAsync();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    [Fact]
    public async Task SaveAndLoad_WithValidMembership_PreservesAllFields()
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Organization organization = CreateOrganization();
        User user = CreateUser();
        OrganizationMembership membership = new(
            organization.Id,
            user.Id,
            OrganizationRole.Administrator,
            CreatedAt);

        dbContext.AddRange(organization, user, membership);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        OrganizationMembership persistedMembership =
            await dbContext.OrganizationMemberships
                .AsNoTracking()
                .SingleAsync();

        Assert.Equal(membership.Id, persistedMembership.Id);
        Assert.Equal(organization.Id, persistedMembership.OrganizationId);
        Assert.Equal(user.Id, persistedMembership.UserId);
        Assert.Equal(OrganizationRole.Administrator, persistedMembership.Role);
        Assert.True(persistedMembership.IsActive);
        Assert.Equal(CreatedAt, persistedMembership.CreatedAt);
    }

    [Fact]
    public async Task SaveAndLoad_WithDeactivatedMembership_PreservesInactiveState()
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Organization organization = CreateOrganization();
        User user = CreateUser();
        OrganizationMembership membership = new(
            organization.Id,
            user.Id,
            OrganizationRole.Member,
            CreatedAt);
        membership.Deactivate();

        dbContext.AddRange(organization, user, membership);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        OrganizationMembership persistedMembership =
            await dbContext.OrganizationMemberships
                .AsNoTracking()
                .SingleAsync();

        Assert.False(persistedMembership.IsActive);
    }

    [Fact]
    public void OrganizationMembershipModel_WithRelationalIdentity_HasExpectedKeysIndexesAndDeleteBehaviors()
    {
        using EnmaDbContext dbContext = fixture.CreateDbContext();
        IEntityType? entityType = dbContext.Model.FindEntityType(
            typeof(OrganizationMembership));

        Assert.NotNull(entityType);
        Assert.Equal(
            [nameof(OrganizationMembership.Id)],
            entityType.FindPrimaryKey()!.Properties
                .Select(property => property.Name)
                .ToArray());

        IKey relationalIdentityKey = Assert.Single(
            entityType.GetKeys(),
            key => key.GetName() ==
                "ak_organization_memberships_organization_id_id");
        Assert.Equal(
            [
                nameof(OrganizationMembership.OrganizationId),
                nameof(OrganizationMembership.Id)
            ],
            relationalIdentityKey.Properties
                .Select(property => property.Name)
                .ToArray());

        IKey organizationUserKey = Assert.Single(
            entityType.GetKeys(),
            key => key.GetName() ==
                "ux_organization_memberships_organization_id_user_id");
        Assert.Equal(
            [
                nameof(OrganizationMembership.OrganizationId),
                nameof(OrganizationMembership.UserId)
            ],
            organizationUserKey.Properties
                .Select(property => property.Name)
                .ToArray());
        Assert.DoesNotContain(
            entityType.GetIndexes(),
            index => index.Properties.Select(property => property.Name)
                .SequenceEqual(
                    [
                        nameof(OrganizationMembership.OrganizationId),
                        nameof(OrganizationMembership.UserId)
                    ]));
        Assert.Equal(3, entityType.GetKeys().Count());

        Assert.Equal(2, entityType.GetForeignKeys().Count());
        Assert.All(
            entityType.GetForeignKeys(),
            foreignKey => Assert.Equal(
                DeleteBehavior.Restrict,
                foreignKey.DeleteBehavior));
    }

    [Fact]
    public async Task SaveChanges_WithDuplicateOrganizationAndUser_ThrowsDbUpdateException()
    {
        Organization organization = CreateOrganization();
        User user = CreateUser();

        await using (EnmaDbContext firstContext = fixture.CreateDbContext())
        {
            OrganizationMembership firstMembership = new(
                organization.Id,
                user.Id,
                OrganizationRole.Owner,
                CreatedAt);
            firstContext.AddRange(organization, user, firstMembership);
            await firstContext.SaveChangesAsync();
        }

        await using EnmaDbContext secondContext = fixture.CreateDbContext();
        OrganizationMembership secondMembership = new(
            organization.Id,
            user.Id,
            OrganizationRole.Member,
            CreatedAt.AddMinutes(1));
        secondContext.OrganizationMemberships.Add(secondMembership);

        DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => secondContext.SaveChangesAsync());

        AssertPostgresException(
            exception,
            PostgresErrorCodes.UniqueViolation,
            "ux_organization_memberships_organization_id_user_id");
    }

    [Fact]
    public async Task SaveChanges_WithSameUserInDifferentOrganizations_PersistsBothMemberships()
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Organization firstOrganization = CreateOrganization();
        Organization secondOrganization = new(
            "Second Legal",
            "second-legal",
            CreatedAt.AddMinutes(1));
        User user = CreateUser();
        OrganizationMembership firstMembership = new(
            firstOrganization.Id,
            user.Id,
            OrganizationRole.Owner,
            CreatedAt);
        OrganizationMembership secondMembership = new(
            secondOrganization.Id,
            user.Id,
            OrganizationRole.Member,
            CreatedAt.AddMinutes(1));

        dbContext.AddRange(
            firstOrganization,
            secondOrganization,
            user,
            firstMembership,
            secondMembership);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        OrganizationMembership[] persistedMemberships =
            await dbContext.OrganizationMemberships
                .AsNoTracking()
                .OrderBy(membership => membership.OrganizationId)
                .ToArrayAsync();

        Assert.Equal(2, persistedMemberships.Length);
        Assert.All(
            persistedMemberships,
            membership => Assert.Equal(user.Id, membership.UserId));
        Assert.Contains(
            persistedMemberships,
            membership => membership.OrganizationId == firstOrganization.Id);
        Assert.Contains(
            persistedMemberships,
            membership => membership.OrganizationId == secondOrganization.Id);
    }

    [Fact]
    public async Task SaveChanges_WithDifferentUsersInSameOrganization_PersistsBothMemberships()
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Organization organization = CreateOrganization();
        User firstUser = CreateUser();
        User secondUser = new(
            "Second User",
            "second@example.com",
            CreatedAt.AddMinutes(1));
        OrganizationMembership firstMembership = new(
            organization.Id,
            firstUser.Id,
            OrganizationRole.Owner,
            CreatedAt);
        OrganizationMembership secondMembership = new(
            organization.Id,
            secondUser.Id,
            OrganizationRole.Administrator,
            CreatedAt.AddMinutes(1));

        dbContext.AddRange(
            organization,
            firstUser,
            secondUser,
            firstMembership,
            secondMembership);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        OrganizationMembership[] persistedMemberships =
            await dbContext.OrganizationMemberships
                .AsNoTracking()
                .OrderBy(membership => membership.UserId)
                .ToArrayAsync();

        Assert.Equal(2, persistedMemberships.Length);
        Assert.All(
            persistedMemberships,
            membership => Assert.Equal(
                organization.Id,
                membership.OrganizationId));
        Assert.Contains(
            persistedMemberships,
            membership => membership.UserId == firstUser.Id);
        Assert.Contains(
            persistedMemberships,
            membership => membership.UserId == secondUser.Id);
    }

    [Fact]
    public async Task SaveChanges_WithMissingOrganization_ThrowsDbUpdateException()
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        User user = CreateUser();
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();

        OrganizationMembership membership = new(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            user.Id,
            OrganizationRole.Member,
            CreatedAt);
        dbContext.OrganizationMemberships.Add(membership);

        DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => dbContext.SaveChangesAsync());

        AssertPostgresException(
            exception,
            PostgresErrorCodes.ForeignKeyViolation,
            "fk_organization_memberships_organizations_organization_id");
    }

    [Fact]
    public async Task SaveChanges_WithMissingUser_ThrowsDbUpdateException()
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Organization organization = CreateOrganization();
        dbContext.Organizations.Add(organization);
        await dbContext.SaveChangesAsync();

        OrganizationMembership membership = new(
            organization.Id,
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            OrganizationRole.Member,
            CreatedAt);
        dbContext.OrganizationMemberships.Add(membership);

        DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => dbContext.SaveChangesAsync());

        AssertPostgresException(
            exception,
            PostgresErrorCodes.ForeignKeyViolation,
            "fk_organization_memberships_users_user_id");
    }

    [Fact]
    public async Task Insert_WithUndefinedRole_IsRejectedByDatabase()
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Organization organization = CreateOrganization();
        User user = CreateUser();
        dbContext.AddRange(organization, user);
        await dbContext.SaveChangesAsync();

        Guid membershipId = Guid.Parse(
            "33333333-3333-3333-3333-333333333333");
        const int UndefinedRole = 999;

        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
            async () => await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO organization_memberships
                    (id, organization_id, user_id, role, is_active, created_at)
                VALUES
                    ({membershipId}, {organization.Id}, {user.Id}, {UndefinedRole}, {true}, {CreatedAt})
                """));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal(
            "ck_organization_memberships_role",
            exception.ConstraintName);
    }

    private static Organization CreateOrganization()
    {
        return new Organization("Enma Legal", "enma-legal", CreatedAt);
    }

    private static User CreateUser()
    {
        return new User("Enma User", "user@example.com", CreatedAt);
    }

    private static void AssertPostgresException(
        DbUpdateException exception,
        string expectedSqlState,
        string expectedConstraintName)
    {
        PostgresException postgresException =
            Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal(expectedSqlState, postgresException.SqlState);
        Assert.Equal(expectedConstraintName, postgresException.ConstraintName);
    }
}
