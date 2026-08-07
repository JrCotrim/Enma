using Enma.Application.Abstractions;
using Enma.Domain.Authentication;
using Enma.Domain.Organizations;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Enma.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Enma.IntegrationTests.Infrastructure.Persistence.Repositories;

[Collection(PostgreSqlCollection.Name)]
public sealed class AuthenticationSessionRepositoryTests(PostgreSqlFixture fixture)
    : IAsyncLifetime
{
    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        5,
        6,
        7,
        8,
        9,
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
    public async Task AddAsync_ThenUnitOfWorkSave_PersistsSessionAndGetByIdReturnsTrackedEntity()
    {
        User user = new(
            "Session Repository User",
            "session-repository@example.com",
            CreatedAt);

        await using (EnmaDbContext seedingContext = fixture.CreateDbContext())
        {
            seedingContext.Users.Add(user);
            await seedingContext.SaveChangesAsync();
        }

        var session = new AuthenticationSession(
            user.Id,
            CreateSecretHash(),
            1,
            CreatedAt,
            CreatedAt.AddMinutes(30),
            CreatedAt.AddHours(2));

        await using (EnmaDbContext addContext = fixture.CreateDbContext())
        {
            var repository = new AuthenticationSessionRepository(addContext);
            IUnitOfWork unitOfWork = addContext;

            await repository.AddAsync(session);

            await using (EnmaDbContext beforeSaveContext = fixture.CreateDbContext())
            {
                Assert.False(await beforeSaveContext.AuthenticationSessions
                    .AsNoTracking()
                    .AnyAsync(candidate => candidate.Id == session.Id));
            }

            await unitOfWork.SaveChangesAsync();
        }

        await using (EnmaDbContext verificationContext = fixture.CreateDbContext())
        {
            Assert.True(await verificationContext.AuthenticationSessions
                .AsNoTracking()
                .AnyAsync(candidate => candidate.Id == session.Id));
        }

        await using EnmaDbContext trackedContext = fixture.CreateDbContext();
        var trackedRepository = new AuthenticationSessionRepository(trackedContext);

        AuthenticationSession? firstResult = await trackedRepository.GetByIdAsync(
            session.Id);
        AuthenticationSession? secondResult = await trackedRepository.GetByIdAsync(
            session.Id);

        Assert.NotNull(firstResult);
        Assert.Same(firstResult, secondResult);
        var trackedEntry = Assert.Single(
            trackedContext.ChangeTracker.Entries<AuthenticationSession>());
        Assert.Same(firstResult, trackedEntry.Entity);
        Assert.Equal(EntityState.Unchanged, trackedEntry.State);
    }

    [Fact]
    public async Task GetByIdAsync_WithUnknownId_ReturnsNull()
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        var repository = new AuthenticationSessionRepository(dbContext);
        using var cancellationTokenSource = new CancellationTokenSource();
        Guid unknownId = Guid.Parse("bbb0ff87-73db-4e38-b58c-d732cfb6ad04");

        AuthenticationSession? result = await repository.GetByIdAsync(
            unknownId,
            cancellationTokenSource.Token);

        Assert.Null(result);
        Assert.Empty(dbContext.ChangeTracker.Entries<AuthenticationSession>());
    }

    [Fact]
    public async Task GetBySecretHashAsync_WithExistingEquivalentHash_ReturnsTrackedSession()
    {
        User user = new(
            "Secret Hash Lookup User",
            "secret-hash-lookup@example.com",
            CreatedAt);
        AuthenticationSessionSecretHash persistedHash = CreateSecretHash();
        var session = new AuthenticationSession(
            user.Id,
            persistedHash,
            1,
            CreatedAt,
            CreatedAt.AddMinutes(30),
            CreatedAt.AddHours(2));

        await using (EnmaDbContext seedingContext = fixture.CreateDbContext())
        {
            seedingContext.Users.Add(user);
            seedingContext.AuthenticationSessions.Add(session);
            await seedingContext.SaveChangesAsync();
        }

        var lookupHash = new AuthenticationSessionSecretHash(
            persistedHash.ToArray());
        Assert.NotSame(persistedHash, lookupHash);

        await using EnmaDbContext lookupContext = fixture.CreateDbContext();
        var repository = new AuthenticationSessionRepository(lookupContext);

        AuthenticationSession? result = await repository.GetBySecretHashAsync(
            lookupHash);

        Assert.NotNull(result);
        Assert.Equal(session.Id, result.Id);
        Assert.Equal(user.Id, result.UserId);
        Assert.Equal(lookupHash, result.SecretHash);
        var trackedEntry = Assert.Single(
            lookupContext.ChangeTracker.Entries<AuthenticationSession>());
        Assert.Same(result, trackedEntry.Entity);
        Assert.Equal(EntityState.Unchanged, trackedEntry.State);
        Assert.Empty(lookupContext.ChangeTracker.Entries<User>());
        Assert.Empty(lookupContext.ChangeTracker.Entries<UserCredential>());
        Assert.Empty(
            lookupContext.ChangeTracker.Entries<OrganizationMembership>());
        Assert.Empty(lookupContext.ChangeTracker.Entries<Organization>());
    }

    [Fact]
    public async Task GetBySecretHashAsync_WithUnknownHash_ReturnsNull()
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        var repository = new AuthenticationSessionRepository(dbContext);

        AuthenticationSession? result = await repository.GetBySecretHashAsync(
            CreateSecretHash());

        Assert.Null(result);
        Assert.Empty(dbContext.ChangeTracker.Entries<AuthenticationSession>());
        Assert.Empty(dbContext.ChangeTracker.Entries());
    }

    private static AuthenticationSessionSecretHash CreateSecretHash()
    {
        byte[] value = Enumerable.Range(65, 32)
            .Select(number => (byte)number)
            .ToArray();

        return new AuthenticationSessionSecretHash(value);
    }
}
