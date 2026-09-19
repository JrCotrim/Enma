using Enma.Application.Authentication;
using Enma.Application.Onboarding;
using Enma.Domain.Organizations;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Enma.IntegrationTests.Infrastructure.Persistence;

[Collection(PostgreSqlCollection.Name)]
public sealed class ExternalAuthenticationPersistenceTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    public Task InitializeAsync() => fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Complete_SameSubjectWithChangedEmailAndMissingName_ContinuesOriginalUser()
    {
        var persistence = CreateExternalPersistence();
        ExternalAuthenticationPersistenceResult first = await persistence.CompleteAsync(
            "Google",
            "subject-a",
            "first@example.test",
            "First User",
            invitationTokenHash: null);
        ExternalAuthenticationPersistenceResult second = await persistence.CompleteAsync(
            "Google",
            "subject-a",
            "second@example.test",
            name: null,
            invitationTokenHash: null);

        Assert.Equal(ExternalAuthenticationPersistenceStatus.Succeeded, first.Status);
        Assert.Equal(first.UserId, second.UserId);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        User user = await dbContext.Users.AsNoTracking().SingleAsync();
        Assert.Equal("first@example.test", user.Email);
        Assert.Single(await dbContext.ExternalIdentities.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Complete_NewIdentityWithoutName_RequiresProfileBeforePersistence()
    {
        ExternalAuthenticationPersistenceResult result =
            await CreateExternalPersistence().CompleteAsync(
                "Google",
                "new-subject-without-name",
                "new@example.test",
                name: null,
                invitationTokenHash: null);

        Assert.Equal(
            ExternalAuthenticationPersistenceStatus.ProfileRequired,
            result.Status);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Empty(await dbContext.Users.AsNoTracking().ToListAsync());
        Assert.Empty(await dbContext.UserCredentials.AsNoTracking().ToListAsync());
        Assert.Empty(await dbContext.ExternalIdentities.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Complete_MatchingExistingEmail_RequiresExplicitLink()
    {
        var user = new User("Local User", "local@example.test", Now);
        user.VerifyEmail(Now);
        await using (EnmaDbContext dbContext = fixture.CreateDbContext())
        {
            dbContext.AddRange(user, new UserCredential(user.Id, "hash", Now));
            await dbContext.SaveChangesAsync();
        }

        ExternalAuthenticationPersistenceResult result =
            await CreateExternalPersistence().CompleteAsync(
                "Google",
                "new-subject",
                user.Email,
                user.Name,
                invitationTokenHash: null);

        Assert.Equal(ExternalAuthenticationPersistenceStatus.LinkRequired, result.Status);
        await using EnmaDbContext assertionContext = fixture.CreateDbContext();
        Assert.Empty(await assertionContext.ExternalIdentities.ToListAsync());
        Assert.Single(await assertionContext.Users.ToListAsync());
    }

    [Fact]
    public async Task Link_DifferentLocalUserOrTakenSubject_FailsClosed()
    {
        User first = await SeedLocalUserAsync("first@example.test");
        User second = await SeedLocalUserAsync("second@example.test");
        ExternalAuthenticationPersistence persistence = CreateExternalPersistence();

        Assert.True(await persistence.LinkAsync(
            first.Id,
            "Google",
            "subject-a",
            first.Email));
        Assert.False(await persistence.LinkAsync(
            second.Id,
            "Google",
            "subject-a",
            first.Email));
        Assert.False(await persistence.LinkAsync(
            second.Id,
            "Google",
            "subject-a",
            second.Email));

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        ExternalIdentity identity = await dbContext.ExternalIdentities
            .AsNoTracking()
            .SingleAsync();
        Assert.Equal(first.Id, identity.UserId);
    }

    [Fact]
    public async Task Link_ConcurrentSameSubject_AttachesToAtMostOneUser()
    {
        User first = await SeedLocalUserAsync("first-link@example.test");
        User second = await SeedLocalUserAsync("second-link@example.test");

        bool[] results = await Task.WhenAll(
            CreateExternalPersistence().LinkAsync(
                first.Id,
                "Google",
                "concurrent-link-subject",
                first.Email),
            CreateExternalPersistence().LinkAsync(
                second.Id,
                "Google",
                "concurrent-link-subject",
                second.Email));

        Assert.Single(results, linked => linked);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Single(await dbContext.ExternalIdentities.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Complete_ConcurrentSameSubject_CreatesExactlyOneUserAndLink()
    {
        Task<ExternalAuthenticationPersistenceResult>[] attempts =
        [
            CreateExternalPersistence().CompleteAsync(
                "Google", "concurrent-subject", "same@example.test", "Same User", null),
            CreateExternalPersistence().CompleteAsync(
                "Google", "concurrent-subject", "same@example.test", "Same User", null)
        ];

        ExternalAuthenticationPersistenceResult[] results = await Task.WhenAll(attempts);

        Assert.All(results, result => Assert.Equal(
            ExternalAuthenticationPersistenceStatus.Succeeded,
            result.Status));
        Assert.Equal(results[0].UserId, results[1].UserId);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Single(await dbContext.Users.AsNoTracking().ToListAsync());
        Assert.Single(await dbContext.ExternalIdentities.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Complete_WrongInvitationRecipient_CreatesNothingAndPreservesInvite()
    {
        var organization = new Organization("Inviting Firm", "inviting-firm", Now);
        var owner = new User("Owner", "owner@firm.test", Now);
        owner.VerifyEmail(Now);
        var ownerCredential = new UserCredential(owner.Id, "hash", Now);
        var ownerMembership = new OrganizationMembership(
            organization.Id,
            owner.Id,
            OrganizationRole.Owner,
            Now);
        var tokenHash = new OrganizationInvitationTokenHash(
            Enumerable.Repeat((byte)7, 32).ToArray());
        var invitation = new OrganizationInvitation(
            organization.Id,
            "invited@example.test",
            OrganizationRole.Administrator,
            ownerMembership.Id,
            tokenHash,
            Now,
            Now,
            Now.AddDays(1));
        await using (EnmaDbContext dbContext = fixture.CreateDbContext())
        {
            dbContext.AddRange(
                organization,
                owner,
                ownerCredential,
                ownerMembership,
                invitation);
            await dbContext.SaveChangesAsync();
        }

        ExternalAuthenticationPersistenceResult result =
            await CreateExternalPersistence().CompleteAsync(
                "Google",
                "wrong-recipient-subject",
                "different@example.test",
                "Different User",
                tokenHash);

        Assert.Equal(
            ExternalAuthenticationPersistenceStatus.WrongInvitationRecipient,
            result.Status);
        await using EnmaDbContext assertionContext = fixture.CreateDbContext();
        Assert.Single(await assertionContext.Users.AsNoTracking().ToListAsync());
        Assert.Empty(await assertionContext.ExternalIdentities.AsNoTracking().ToListAsync());
        OrganizationInvitation preserved = await assertionContext
            .OrganizationInvitations.AsNoTracking().SingleAsync();
        Assert.Equal(OrganizationInvitationState.Pending, preserved.GetState(Now));
        Assert.NotNull(preserved.TokenHash);
        Assert.Empty(await assertionContext.OrganizationMemberships
            .Where(membership => membership.UserId != owner.Id)
            .ToListAsync());
    }

    [Fact]
    public async Task Complete_LinkedSubjectWithWrongInvitationRecipient_BlocksInviteFlow()
    {
        ExternalAuthenticationPersistence persistence = CreateExternalPersistence();
        ExternalAuthenticationPersistenceResult first = await persistence.CompleteAsync(
            "Google",
            "linked-subject",
            "linked@example.test",
            "Linked User",
            invitationTokenHash: null);
        OrganizationInvitationTokenHash tokenHash =
            await SeedInvitationAsync("invited@example.test");

        ExternalAuthenticationPersistenceResult result = await persistence.CompleteAsync(
            "Google",
            "linked-subject",
            "different@example.test",
            "Linked User",
            tokenHash);

        Assert.Equal(
            ExternalAuthenticationPersistenceStatus.WrongInvitationRecipient,
            result.Status);
        Assert.Equal(ExternalAuthenticationPersistenceStatus.Succeeded, first.Status);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        OrganizationInvitation invitation = await dbContext.OrganizationInvitations
            .AsNoTracking()
            .SingleAsync();
        Assert.Equal(OrganizationInvitationState.Pending, invitation.GetState(Now));
        Assert.NotNull(invitation.TokenHash);
        Assert.DoesNotContain(
            await dbContext.OrganizationMemberships.AsNoTracking().ToListAsync(),
            membership => membership.UserId == first.UserId);
    }

    [Fact]
    public async Task Complete_NewGoogleInvite_PreservesInviteForExistingAcceptance()
    {
        OrganizationInvitationTokenHash tokenHash =
            await SeedInvitationAsync("invited@example.test");

        ExternalAuthenticationPersistenceResult result =
            await CreateExternalPersistence().CompleteAsync(
                "Google",
                "new-invited-subject",
                "invited@example.test",
                "Invited User",
                tokenHash);

        Assert.Equal(ExternalAuthenticationPersistenceStatus.Succeeded, result.Status);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        OrganizationInvitation invitation = await dbContext.OrganizationInvitations
            .AsNoTracking()
            .SingleAsync();
        Assert.Equal(OrganizationInvitationState.Pending, invitation.GetState(Now));
        Assert.Equal(OrganizationRole.Member, invitation.Role);
        Assert.NotNull(invitation.TokenHash);
        Assert.Single(await dbContext.Organizations.AsNoTracking().ToListAsync());
        Assert.DoesNotContain(
            await dbContext.OrganizationMemberships.AsNoTracking().ToListAsync(),
            membership => membership.UserId == result.UserId);
        Assert.Equal(
            result.UserId,
            (await dbContext.ExternalIdentities.AsNoTracking().SingleAsync()).UserId);
    }

    [Fact]
    public async Task Complete_LinkedGoogleInvite_ResumesWithoutCreatingTenantOrMembership()
    {
        ExternalAuthenticationPersistence persistence = CreateExternalPersistence();
        ExternalAuthenticationPersistenceResult first = await persistence.CompleteAsync(
            "Google",
            "linked-invited-subject",
            "linked-invited@example.test",
            "Linked Invited User",
            invitationTokenHash: null);
        OrganizationInvitationTokenHash tokenHash =
            await SeedInvitationAsync("linked-invited@example.test");

        ExternalAuthenticationPersistenceResult resumed =
            await persistence.CompleteAsync(
                "Google",
                "linked-invited-subject",
                "linked-invited@example.test",
                name: null,
                tokenHash);

        Assert.Equal(ExternalAuthenticationPersistenceStatus.Succeeded, resumed.Status);
        Assert.Equal(first.UserId, resumed.UserId);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Single(await dbContext.Organizations.AsNoTracking().ToListAsync());
        Assert.DoesNotContain(
            await dbContext.OrganizationMemberships.AsNoTracking().ToListAsync(),
            membership => membership.UserId == resumed.UserId);
        OrganizationInvitation invitation = await dbContext.OrganizationInvitations
            .AsNoTracking()
            .SingleAsync();
        Assert.Equal(OrganizationInvitationState.Pending, invitation.GetState(Now));
        Assert.Equal(OrganizationRole.Member, invitation.Role);
    }

    [Fact]
    public async Task InitialOrganization_ConcurrentRequests_CreateExactlyOneOwnerTenant()
    {
        ExternalAuthenticationPersistenceResult googleUser =
            await CreateExternalPersistence().CompleteAsync(
                "Google", "owner-subject", "owner@example.test", "Owner User", null);
        Guid userId = googleUser.UserId!.Value;
        var persistence = new InitialOrganizationPersistence(
            CreateOptions(),
            new FixedTimeProvider(Now.AddMinutes(1)));

        InitialOrganizationResult[] results = await Task.WhenAll(
            persistence.CreateAsync(
                userId,
                "Google",
                "owner-subject",
                "First Firm",
                "first-firm"),
            persistence.CreateAsync(
                userId,
                "Google",
                "owner-subject",
                "Second Firm",
                "second-firm"));

        Assert.Single(results, result => result.Status == InitialOrganizationStatus.Succeeded);
        Assert.Single(results, result => result.Status == InitialOrganizationStatus.Ineligible);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Single(await dbContext.Organizations.AsNoTracking().ToListAsync());
        var membership = await dbContext.OrganizationMemberships
            .AsNoTracking()
            .SingleAsync();
        Assert.Equal(userId, membership.UserId);
        Assert.Equal(Enma.Domain.Organizations.OrganizationRole.Owner, membership.Role);
    }

    [Fact]
    public async Task InitialOrganization_LocalOnlyUser_IsIneligible()
    {
        User user = await SeedLocalUserAsync("local-only@example.test");
        var persistence = new InitialOrganizationPersistence(
            CreateOptions(),
            new FixedTimeProvider(Now));

        InitialOrganizationResult result = await persistence.CreateAsync(
            user.Id,
            "Google",
            "missing-subject",
            "Local Firm",
            "local-firm");

        Assert.Equal(InitialOrganizationStatus.Ineligible, result.Status);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Empty(await dbContext.Organizations.AsNoTracking().ToListAsync());
        Assert.Empty(await dbContext.OrganizationMemberships.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task InitialOrganization_MismatchedExternalHandoff_IsIneligible()
    {
        ExternalAuthenticationPersistenceResult googleUser =
            await CreateExternalPersistence().CompleteAsync(
                "Google",
                "invited-subject",
                "invited@example.test",
                "Invited User",
                invitationTokenHash: null);
        var persistence = new InitialOrganizationPersistence(
            CreateOptions(),
            new FixedTimeProvider(Now));

        InitialOrganizationResult result = await persistence.CreateAsync(
            googleUser.UserId!.Value,
            "Google",
            "different-subject",
            "Unexpected Firm",
            "unexpected-firm");

        Assert.Equal(InitialOrganizationStatus.Ineligible, result.Status);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Empty(await dbContext.Organizations.AsNoTracking().ToListAsync());
        Assert.Empty(await dbContext.OrganizationMemberships
            .AsNoTracking()
            .ToListAsync());
    }

    private async Task<User> SeedLocalUserAsync(string email)
    {
        var user = new User("Local User", email, Now);
        user.VerifyEmail(Now);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.AddRange(user, new UserCredential(user.Id, "hash", Now));
        await dbContext.SaveChangesAsync();
        return user;
    }

    private async Task<OrganizationInvitationTokenHash> SeedInvitationAsync(
        string invitedEmail)
    {
        var organization = new Organization(
            "Inviting Firm",
            $"inviting-{Guid.NewGuid():N}",
            Now);
        User owner = await SeedLocalUserAsync($"owner-{Guid.NewGuid():N}@firm.test");
        var ownerMembership = new OrganizationMembership(
            organization.Id,
            owner.Id,
            OrganizationRole.Owner,
            Now);
        var tokenHash = new OrganizationInvitationTokenHash(
            Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).ToArray());
        var invitation = new OrganizationInvitation(
            organization.Id,
            invitedEmail,
            OrganizationRole.Member,
            ownerMembership.Id,
            tokenHash,
            Now,
            Now,
            Now.AddDays(1));
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.AddRange(organization, ownerMembership, invitation);
        await dbContext.SaveChangesAsync();
        return tokenHash;
    }

    private ExternalAuthenticationPersistence CreateExternalPersistence() =>
        new(CreateOptions(), new FixedTimeProvider(Now));

    private DbContextOptions<EnmaDbContext> CreateOptions() =>
        new DbContextOptionsBuilder<EnmaDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .Options;

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
