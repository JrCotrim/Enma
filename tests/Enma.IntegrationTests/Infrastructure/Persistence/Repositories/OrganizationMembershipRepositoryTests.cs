using Enma.Application.Abstractions;
using Enma.Domain.Organizations;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Enma.Infrastructure.Persistence.Repositories;
using Enma.IntegrationTests.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Enma.IntegrationTests.Infrastructure.Persistence.Repositories;

[Collection(PostgreSqlCollection.Name)]
public sealed class OrganizationMembershipRepositoryTests(PostgreSqlFixture fixture)
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
    public async Task AddAsync_ThenUnitOfWorkSave_PersistsMembership()
    {
        Organization organization = new("Enma Legal", "enma-legal", CreatedAt);
        User user = new("Enma User", "user@example.com", CreatedAt);

        await using (EnmaDbContext seedContext = fixture.CreateDbContext())
        {
            seedContext.AddRange(organization, user);
            await seedContext.SaveChangesAsync();
        }

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        OrganizationMembershipRepository repository = new(dbContext);
        IUnitOfWork unitOfWork = dbContext;
        OrganizationMembership membership = new(
            organization.Id,
            user.Id,
            OrganizationRole.Owner,
            CreatedAt);

        await repository.AddAsync(membership);

        await using (EnmaDbContext beforeSaveContext = fixture.CreateDbContext())
        {
            bool existsBeforeSave = await beforeSaveContext.OrganizationMemberships
                .AsNoTracking()
                .AnyAsync(candidate => candidate.Id == membership.Id);

            Assert.False(existsBeforeSave);
        }

        await unitOfWork.SaveChangesAsync();

        await using EnmaDbContext verificationContext = fixture.CreateDbContext();
        OrganizationMembership persistedMembership =
            await verificationContext.OrganizationMemberships
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Id == membership.Id);

        Assert.Equal(membership.Id, persistedMembership.Id);
        Assert.Equal(organization.Id, persistedMembership.OrganizationId);
        Assert.Equal(user.Id, persistedMembership.UserId);
        Assert.Equal(OrganizationRole.Owner, persistedMembership.Role);
        Assert.True(persistedMembership.IsActive);
        Assert.Equal(CreatedAt, persistedMembership.CreatedAt);
    }
}
