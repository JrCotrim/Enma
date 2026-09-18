using Enma.Application.Onboarding.RegisterInvitedUser;
using Enma.Domain.Authentication;
using Enma.Domain.Organizations;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Enma.IntegrationTests.Infrastructure.Persistence;

[Collection(PostgreSqlCollection.Name)]
public sealed class InvitedUserRegistrationPersistenceTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset CreatedAt =
        new(2026, 9, 17, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = CreatedAt.AddHours(1);

    public Task InitializeAsync() => fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task RegisterAsync_ValidInvite_CreatesIdentityWithoutTenantOrMembership()
    {
        InvitationGraph graph = await SeedInvitationAsync(
            "invitee@example.test",
            OrganizationRole.Member);
        RegistrationEntities registration = CreateRegistration(
            "invitee@example.test");

        InvitedUserRegistrationPersistenceResult result =
            await CreatePersistence().RegisterAsync(
                graph.TokenHash,
                registration.User,
                registration.Credential,
                registration.Challenge);

        Assert.Equal(InvitedUserRegistrationPersistenceResult.Succeeded, result);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Equal(1, await dbContext.Organizations.CountAsync());
        Assert.Equal(2, await dbContext.Users.CountAsync());
        Assert.Equal(1, await dbContext.OrganizationMemberships.CountAsync());
        Assert.False(await dbContext.OrganizationMemberships.AnyAsync(
            membership => membership.UserId == registration.User.Id));
        Assert.True(await dbContext.UserCredentials.AnyAsync(
            credential => credential.UserId == registration.User.Id));
        Assert.True(await dbContext.EmailVerificationChallenges.AnyAsync(
            challenge => challenge.UserId == registration.User.Id));
        OrganizationInvitation invitation = await dbContext
            .OrganizationInvitations.SingleAsync();
        Assert.Equal(OrganizationInvitationState.Pending, invitation.GetState(Now));
        Assert.Equal(graph.TokenHash, invitation.TokenHash);
    }

    [Fact]
    public async Task RegisterAsync_WrongEmail_RejectsWithoutWritesOrConsumption()
    {
        InvitationGraph graph = await SeedInvitationAsync(
            "intended@example.test",
            OrganizationRole.Administrator);
        RegistrationEntities registration = CreateRegistration(
            "wrong@example.test");

        InvitedUserRegistrationPersistenceResult result =
            await CreatePersistence().RegisterAsync(
                graph.TokenHash,
                registration.User,
                registration.Credential,
                registration.Challenge);

        Assert.Equal(
            InvitedUserRegistrationPersistenceResult.WrongRecipient,
            result);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Equal(1, await dbContext.Users.CountAsync());
        Assert.Equal(graph.TokenHash, (await dbContext.OrganizationInvitations
            .SingleAsync()).TokenHash);
    }

    [Fact]
    public async Task RegisterAsync_ExistingUser_DoesNotDuplicateIdentity()
    {
        InvitationGraph graph = await SeedInvitationAsync(
            "existing@example.test",
            OrganizationRole.Member);
        var existingUser = new User(
            "Existing User",
            "existing@example.test",
            CreatedAt);
        await SeedAsync(existingUser);
        RegistrationEntities registration = CreateRegistration(
            "existing@example.test");

        InvitedUserRegistrationPersistenceResult result =
            await CreatePersistence().RegisterAsync(
                graph.TokenHash,
                registration.User,
                registration.Credential,
                registration.Challenge);

        Assert.Equal(InvitedUserRegistrationPersistenceResult.ExistingUser, result);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Equal(2, await dbContext.Users.CountAsync());
        Assert.False(await dbContext.Users.AnyAsync(
            user => user.Id == registration.User.Id));
        Assert.Equal(graph.TokenHash, (await dbContext.OrganizationInvitations
            .SingleAsync()).TokenHash);
    }

    [Fact]
    public async Task RegisterThenVerifyAndAccept_UsesInvitationOrganizationAndRole()
    {
        InvitationGraph graph = await SeedInvitationAsync(
            "invitee@example.test",
            OrganizationRole.Administrator);
        RegistrationEntities registration = CreateRegistration(
            "invitee@example.test");
        InvitedUserRegistrationPersistence persistence = CreatePersistence();

        Assert.Equal(
            InvitedUserRegistrationPersistenceResult.Succeeded,
            await persistence.RegisterAsync(
                graph.TokenHash,
                registration.User,
                registration.Credential,
                registration.Challenge));

        await using (EnmaDbContext verificationContext = fixture.CreateDbContext())
        {
            User user = await verificationContext.Users.SingleAsync(
                candidate => candidate.Id == registration.User.Id);
            user.VerifyEmail(Now);
            await verificationContext.SaveChangesAsync();
        }

        var invitationPersistence = new OrganizationInvitationMutationPersistence(
            new DbContextOptionsBuilder<EnmaDbContext>()
                .UseNpgsql(fixture.ConnectionString)
                .Options,
            new FixedTimeProvider(Now),
            new NoopInvitationTokenService());

        Assert.Equal(
            Enma.Application.Organizations.Invitations
                .AcceptOrganizationInvitationPersistenceResult.Succeeded,
            await invitationPersistence.AcceptAsync(
                registration.User.Id,
                graph.TokenHash));

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        OrganizationMembership membership = await dbContext
            .OrganizationMemberships.SingleAsync(candidate =>
                candidate.UserId == registration.User.Id);
        Assert.Equal(graph.Organization.Id, membership.OrganizationId);
        Assert.Equal(OrganizationRole.Administrator, membership.Role);
        Assert.NotEqual(OrganizationRole.Owner, membership.Role);
        Assert.Equal(1, await dbContext.Organizations.CountAsync());
    }

    [Fact]
    public async Task RegisterAsync_ConcurrentAttempts_CreateOneUser()
    {
        InvitationGraph graph = await SeedInvitationAsync(
            "invitee@example.test",
            OrganizationRole.Member);
        RegistrationEntities first = CreateRegistration("invitee@example.test");
        RegistrationEntities second = CreateRegistration("invitee@example.test");
        InvitedUserRegistrationPersistence persistence = CreatePersistence();

        InvitedUserRegistrationPersistenceResult[] results = await Task.WhenAll(
            persistence.RegisterAsync(
                graph.TokenHash,
                first.User,
                first.Credential,
                first.Challenge),
            persistence.RegisterAsync(
                graph.TokenHash,
                second.User,
                second.Credential,
                second.Challenge));

        Assert.Single(results, result =>
            result == InvitedUserRegistrationPersistenceResult.Succeeded);
        Assert.Single(results, result =>
            result == InvitedUserRegistrationPersistenceResult.ExistingUser);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Equal(1, await dbContext.Users.CountAsync(
            user => user.Email == "invitee@example.test"));
        Assert.Equal(1, await dbContext.Organizations.CountAsync());
        Assert.Equal(1, await dbContext.OrganizationMemberships.CountAsync());
    }

    private InvitedUserRegistrationPersistence CreatePersistence()
    {
        return new InvitedUserRegistrationPersistence(
            new DbContextOptionsBuilder<EnmaDbContext>()
                .UseNpgsql(fixture.ConnectionString)
                .Options,
            new FixedTimeProvider(Now));
    }

    private async Task<InvitationGraph> SeedInvitationAsync(
        string invitedEmail,
        OrganizationRole role)
    {
        string suffix = Guid.NewGuid().ToString("N");
        var organization = new Organization(
            "Inviting Organization",
            $"inviting-{suffix}",
            CreatedAt);
        var owner = new User(
            "Inviting Owner",
            $"owner-{suffix}@example.test",
            CreatedAt);
        var ownerMembership = new OrganizationMembership(
            organization.Id,
            owner.Id,
            OrganizationRole.Owner,
            CreatedAt);
        var tokenHash = new OrganizationInvitationTokenHash(
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var invitation = new OrganizationInvitation(
            organization.Id,
            invitedEmail,
            role,
            ownerMembership.Id,
            tokenHash,
            CreatedAt,
            CreatedAt,
            Now.AddDays(1));
        await SeedAsync(organization, owner, ownerMembership, invitation);
        return new InvitationGraph(organization, tokenHash);
    }

    private static RegistrationEntities CreateRegistration(string email)
    {
        var user = new User("Invited User", email, Now);
        var credential = new UserCredential(
            user.Id,
            "synthetic-password-hash",
            Now);
        var challenge = new EmailVerificationChallenge(
            user.Id,
            user.Email,
            new EmailVerificationTokenHash(
                System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
            Now,
            Now.AddHours(1));
        return new RegistrationEntities(user, credential, challenge);
    }

    private async Task SeedAsync(params object[] entities)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.AddRange(entities);
        await dbContext.SaveChangesAsync();
    }

    private sealed record InvitationGraph(
        Organization Organization,
        OrganizationInvitationTokenHash TokenHash);

    private sealed record RegistrationEntities(
        User User,
        UserCredential Credential,
        EmailVerificationChallenge Challenge);

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class NoopInvitationTokenService
        : Enma.Application.Organizations.Invitations
            .IOrganizationInvitationTokenService
    {
        public string GenerateToken(
            out OrganizationInvitationTokenHash tokenHash)
        {
            tokenHash = new OrganizationInvitationTokenHash(new byte[32]);
            return new string('a', 43);
        }

        public bool TryHashToken(
            string? rawToken,
            out OrganizationInvitationTokenHash? tokenHash)
        {
            tokenHash = null;
            return false;
        }
    }
}
