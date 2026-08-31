using System.Data.Common;
using Enma.Application.Organizations.Invitations;
using Enma.Domain.Auditing;
using Enma.Domain.Organizations;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Enma.Infrastructure.Persistence.Queries;
using Enma.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Enma.IntegrationTests.Infrastructure.Persistence;

[Collection(PostgreSqlCollection.Name)]
public sealed class OrganizationInvitationMutationPersistenceTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        8,
        30,
        8,
        0,
        0,
        TimeSpan.Zero);
    private static readonly DateTimeOffset Now = CreatedAt.AddHours(2);

    public Task InitializeAsync() => fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task CreateAsync_ValidOwnerInvite_PersistsTokenAndAtomicAudit()
    {
        TestGraph graph = await SeedGraphAsync(OrganizationRole.Owner);
        OrganizationInvitationMutationPersistence persistence = CreatePersistence();

        CreateOrganizationInvitationPersistenceResult result =
            await persistence.CreateAsync(new(
                graph.Actor.Id,
                graph.Organization.Id,
                graph.Membership.Id,
                " New.Member@Example.Test ",
                OrganizationRole.Administrator));

        Assert.Equal(
            CreateOrganizationInvitationPersistenceStatus.Succeeded,
            result.Status);
        Assert.NotEqual(Guid.Empty, result.InvitationId);
        OrganizationInvitationDeliveryRequest delivery = Assert.IsType<
            OrganizationInvitationDeliveryRequest>(result.DeliveryRequest);
        Assert.Equal("new.member@example.test", delivery.Email);
        Assert.Equal(graph.Organization.Name, delivery.OrganizationName);
        Assert.Equal(OrganizationRole.Administrator, delivery.Role);
        Assert.Equal(Now.AddDays(7), delivery.ExpiresAt);

        var tokenService = new CryptographicOrganizationInvitationTokenService();
        Assert.True(tokenService.TryHashToken(delivery.RawToken, out var tokenHash));

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        OrganizationInvitation invitation = await dbContext
            .OrganizationInvitations
            .SingleAsync();
        AuditLog audit = await dbContext.AuditLogs.SingleAsync();
        Assert.Equal(result.InvitationId, invitation.Id);
        Assert.Equal(tokenHash, invitation.TokenHash);
        Assert.Equal(OrganizationInvitationState.Pending, invitation.GetState(Now));
        Assert.Equal(AuditEventType.OrganizationInvitationCreated, audit.EventType);
        Assert.Equal(invitation.Id, audit.EntityId);
        Assert.Equal(graph.Membership.Id, audit.ActorMembershipId);
        Assert.Equal(OrganizationRole.Owner, audit.ActorRoleAtOccurrence);
        Assert.Equal(
            OrganizationRole.Administrator,
            Assert.IsType<OrganizationInvitationCreatedAuditDetails>(audit.Details)
                .Role);
    }

    [Fact]
    public async Task CreateAsync_ExistingMemberships_ReturnExpectedSafeConflicts()
    {
        TestGraph graph = await SeedGraphAsync(OrganizationRole.Owner);
        var activeUser = new User(
            "Active Invitee",
            "active@example.test",
            CreatedAt);
        var activeMembership = new OrganizationMembership(
            graph.Organization.Id,
            activeUser.Id,
            OrganizationRole.Member,
            CreatedAt);
        var inactiveUser = new User(
            "Inactive Invitee",
            "inactive@example.test",
            CreatedAt);
        var inactiveMembership = new OrganizationMembership(
            graph.Organization.Id,
            inactiveUser.Id,
            OrganizationRole.Administrator,
            CreatedAt);
        inactiveMembership.Deactivate();
        await SeedAsync(
            activeUser,
            activeMembership,
            inactiveUser,
            inactiveMembership);
        OrganizationInvitationMutationPersistence persistence = CreatePersistence();

        CreateOrganizationInvitationPersistenceResult active =
            await persistence.CreateAsync(new(
                graph.Actor.Id,
                graph.Organization.Id,
                graph.Membership.Id,
                activeUser.Email,
                OrganizationRole.Member));
        CreateOrganizationInvitationPersistenceResult incompatible =
            await persistence.CreateAsync(new(
                graph.Actor.Id,
                graph.Organization.Id,
                graph.Membership.Id,
                inactiveUser.Email,
                OrganizationRole.Member));

        Assert.Equal(
            CreateOrganizationInvitationPersistenceStatus
                .ExistingActiveMembership,
            active.Status);
        Assert.Equal(
            CreateOrganizationInvitationPersistenceStatus
                .IncompatibleInactiveMembership,
            incompatible.Status);
        await using EnmaDbContext verification = fixture.CreateDbContext();
        Assert.Equal(0, await verification.OrganizationInvitations.CountAsync());
        Assert.Equal(0, await verification.AuditLogs.CountAsync());
    }

    [Fact]
    public async Task CreateAsync_CompatibleInactiveAndForeignMemberships_DoNotBlock()
    {
        TestGraph current = await SeedGraphAsync(
            OrganizationRole.Owner,
            "current-compatible");
        TestGraph foreign = await SeedGraphAsync(
            OrganizationRole.Owner,
            "foreign-compatible");
        var inactiveUser = new User(
            "Compatible Inactive",
            "compatible-inactive@example.test",
            CreatedAt);
        var inactiveMembership = new OrganizationMembership(
            current.Organization.Id,
            inactiveUser.Id,
            OrganizationRole.Member,
            CreatedAt);
        inactiveMembership.Deactivate();
        var foreignUser = new User(
            "Foreign Member",
            "foreign-member@example.test",
            CreatedAt);
        var foreignMembership = new OrganizationMembership(
            foreign.Organization.Id,
            foreignUser.Id,
            OrganizationRole.Member,
            CreatedAt);
        await SeedAsync(
            inactiveUser,
            inactiveMembership,
            foreignUser,
            foreignMembership);
        OrganizationInvitationMutationPersistence persistence = CreatePersistence();

        CreateOrganizationInvitationPersistenceResult compatible =
            await persistence.CreateAsync(new(
                current.Actor.Id,
                current.Organization.Id,
                current.Membership.Id,
                inactiveUser.Email,
                OrganizationRole.Member));
        CreateOrganizationInvitationPersistenceResult crossTenant =
            await persistence.CreateAsync(new(
                current.Actor.Id,
                current.Organization.Id,
                current.Membership.Id,
                foreignUser.Email,
                OrganizationRole.Member));

        Assert.Equal(
            CreateOrganizationInvitationPersistenceStatus.Succeeded,
            compatible.Status);
        Assert.Equal(
            CreateOrganizationInvitationPersistenceStatus.Succeeded,
            crossTenant.Status);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Equal(2, await dbContext.OrganizationInvitations.CountAsync());
        Assert.Equal(2, await dbContext.AuditLogs.CountAsync());
    }

    [Fact]
    public async Task CreateAsync_ExpiredOpenInvite_MaterializesThenCreatesReplacement()
    {
        TestGraph graph = await SeedGraphAsync(OrganizationRole.Owner);
        OrganizationInvitation expired = CreateInvitation(
            graph,
            "expired@example.test",
            OrganizationRole.Member,
            tokenIssuedAt: Now.AddDays(-8),
            expiresAt: Now.AddDays(-1));
        await SeedAsync(expired);

        CreateOrganizationInvitationPersistenceResult result =
            await CreatePersistence().CreateAsync(new(
                graph.Actor.Id,
                graph.Organization.Id,
                graph.Membership.Id,
                expired.InvitedEmail,
                OrganizationRole.Member));

        Assert.Equal(
            CreateOrganizationInvitationPersistenceStatus.Succeeded,
            result.Status);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        OrganizationInvitation[] invitations = await dbContext
            .OrganizationInvitations
            .OrderBy(invitation => invitation.CreatedAt)
            .ToArrayAsync();
        Assert.Equal(2, invitations.Length);
        Assert.Equal(expired.ExpiresAt, invitations[0].ExpiredAt);
        Assert.Null(invitations[0].TokenHash);
        Assert.Equal(OrganizationInvitationState.Pending, invitations[1].GetState(Now));
        Assert.Equal(1, await dbContext.AuditLogs.CountAsync());
    }

    [Fact]
    public async Task CreateAsync_DuplicatePending_DoesNotMutateOrAudit()
    {
        TestGraph graph = await SeedGraphAsync(OrganizationRole.Owner);
        OrganizationInvitation pending = CreateInvitation(
            graph,
            "pending@example.test",
            OrganizationRole.Member,
            tokenIssuedAt: Now.AddMinutes(-2),
            expiresAt: Now.AddDays(7));
        await SeedAsync(pending);

        CreateOrganizationInvitationPersistenceResult result =
            await CreatePersistence().CreateAsync(new(
                graph.Actor.Id,
                graph.Organization.Id,
                graph.Membership.Id,
                pending.InvitedEmail,
                OrganizationRole.Member));

        Assert.Equal(
            CreateOrganizationInvitationPersistenceStatus
                .DuplicatePendingInvitation,
            result.Status);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Equal(1, await dbContext.OrganizationInvitations.CountAsync());
        Assert.Equal(0, await dbContext.AuditLogs.CountAsync());
    }

    [Fact]
    public async Task RevokeAsync_PendingThenIdempotent_WritesOneAudit()
    {
        TestGraph graph = await SeedGraphAsync(OrganizationRole.Administrator);
        OrganizationInvitation invitation = CreateInvitation(
            graph,
            "revoke@example.test",
            OrganizationRole.Member,
            tokenIssuedAt: Now.AddMinutes(-2),
            expiresAt: Now.AddDays(7));
        await SeedAsync(invitation);
        OrganizationInvitationMutationPersistence persistence = CreatePersistence();
        var request = new OrganizationInvitationMutationPersistenceRequest(
            graph.Actor.Id,
            graph.Organization.Id,
            graph.Membership.Id,
            invitation.Id);

        RevokeOrganizationInvitationPersistenceResult first =
            await persistence.RevokeAsync(request);
        RevokeOrganizationInvitationPersistenceResult second =
            await persistence.RevokeAsync(request);

        Assert.Equal(RevokeOrganizationInvitationPersistenceResult.Succeeded, first);
        Assert.Equal(RevokeOrganizationInvitationPersistenceResult.Succeeded, second);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        OrganizationInvitation stored = await dbContext
            .OrganizationInvitations
            .SingleAsync();
        Assert.Equal(Now, stored.RevokedAt);
        Assert.Null(stored.TokenHash);
        AuditLog audit = await dbContext.AuditLogs.SingleAsync();
        Assert.Equal(AuditEventType.OrganizationInvitationRevoked, audit.EventType);
        Assert.Null(audit.Details);
    }

    [Fact]
    public async Task ResendAsync_AfterCooldown_RotatesOnceAndEnforcesCooldown()
    {
        TestGraph graph = await SeedGraphAsync(OrganizationRole.Owner);
        OrganizationInvitation invitation = CreateInvitation(
            graph,
            "resend@example.test",
            OrganizationRole.Administrator,
            tokenIssuedAt: Now.AddMinutes(-2),
            expiresAt: Now.AddDays(5));
        OrganizationInvitationTokenHash oldHash = invitation.TokenHash!;
        await SeedAsync(invitation);
        OrganizationInvitationMutationPersistence persistence = CreatePersistence();
        var request = new OrganizationInvitationMutationPersistenceRequest(
            graph.Actor.Id,
            graph.Organization.Id,
            graph.Membership.Id,
            invitation.Id);

        ResendOrganizationInvitationPersistenceResult first =
            await persistence.ResendAsync(request);
        ResendOrganizationInvitationPersistenceResult second =
            await persistence.ResendAsync(request);

        Assert.Equal(
            ResendOrganizationInvitationPersistenceStatus.Succeeded,
            first.Status);
        Assert.Equal(
            ResendOrganizationInvitationPersistenceStatus.Cooldown,
            second.Status);
        Assert.Equal(TimeSpan.FromSeconds(60), second.RetryAfter);
        var tokenService = new CryptographicOrganizationInvitationTokenService();
        Assert.True(tokenService.TryHashToken(
            first.DeliveryRequest!.RawToken,
            out var newHash));
        Assert.NotEqual(oldHash, newHash);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        OrganizationInvitation stored = await dbContext
            .OrganizationInvitations
            .SingleAsync();
        Assert.Equal(newHash, stored.TokenHash);
        Assert.Equal(Now, stored.TokenIssuedAt);
        Assert.Equal(Now.AddDays(7), stored.ExpiresAt);
        Assert.Equal(CreatedAt, stored.CreatedAt);
        Assert.Equal(graph.Membership.Id, stored.CreatedByMembershipId);
        AuditLog audit = await dbContext.AuditLogs.SingleAsync();
        Assert.Equal(AuditEventType.OrganizationInvitationResent, audit.EventType);
        Assert.Null(audit.Details);
    }

    [Fact]
    public async Task Mutations_StaleOrEscalatingActor_AreDeniedWithoutAudit()
    {
        TestGraph administrator = await SeedGraphAsync(
            OrganizationRole.Administrator,
            "administrator");
        OrganizationInvitation elevated = CreateInvitation(
            administrator,
            "admin-target@example.test",
            OrganizationRole.Administrator,
            tokenIssuedAt: Now.AddMinutes(-2),
            expiresAt: Now.AddDays(7));
        await SeedAsync(elevated);

        CreateOrganizationInvitationPersistenceResult create =
            await CreatePersistence().CreateAsync(new(
                administrator.Actor.Id,
                administrator.Organization.Id,
                administrator.Membership.Id,
                "new-admin@example.test",
                OrganizationRole.Administrator));
        RevokeOrganizationInvitationPersistenceResult revoke =
            await CreatePersistence().RevokeAsync(new(
                administrator.Actor.Id,
                administrator.Organization.Id,
                administrator.Membership.Id,
                elevated.Id));

        Assert.Equal(
            CreateOrganizationInvitationPersistenceStatus.AccessDenied,
            create.Status);
        Assert.Equal(
            RevokeOrganizationInvitationPersistenceResult.AccessDenied,
            revoke);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Equal(1, await dbContext.OrganizationInvitations.CountAsync());
        Assert.Equal(0, await dbContext.AuditLogs.CountAsync());
    }

    [Fact]
    public async Task CreateAsync_InactiveOrganizationMembershipAndUser_FailClosed()
    {
        TestGraph inactiveOrganization = await SeedGraphAsync(
            OrganizationRole.Owner,
            "inactive-organization");
        TestGraph inactiveMembership = await SeedGraphAsync(
            OrganizationRole.Owner,
            "inactive-membership");
        TestGraph inactiveUser = await SeedGraphAsync(
            OrganizationRole.Owner,
            "inactive-user");
        await using (EnmaDbContext mutation = fixture.CreateDbContext())
        {
            Organization organization = await mutation.Organizations.SingleAsync(
                candidate => candidate.Id == inactiveOrganization.Organization.Id);
            OrganizationMembership membership = await mutation
                .OrganizationMemberships
                .SingleAsync(candidate =>
                    candidate.Id == inactiveMembership.Membership.Id);
            User user = await mutation.Users.SingleAsync(candidate =>
                candidate.Id == inactiveUser.Actor.Id);
            organization.Deactivate();
            membership.Deactivate();
            user.Deactivate();
            await mutation.SaveChangesAsync();
        }

        CreateOrganizationInvitationPersistenceResult[] results =
        [
            await CreatePersistence().CreateAsync(CreateRequest(
                inactiveOrganization,
                "inactive-organization@example.test")),
            await CreatePersistence().CreateAsync(CreateRequest(
                inactiveMembership,
                "inactive-membership@example.test")),
            await CreatePersistence().CreateAsync(CreateRequest(
                inactiveUser,
                "inactive-user@example.test"))
        ];

        Assert.All(
            results,
            result => Assert.Equal(
                CreateOrganizationInvitationPersistenceStatus.AccessDenied,
                result.Status));
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Equal(0, await dbContext.OrganizationInvitations.CountAsync());
        Assert.Equal(0, await dbContext.AuditLogs.CountAsync());
    }

    [Fact]
    public async Task ReadQueries_TenantQualifyCountAndUseStableBoundedOrdering()
    {
        TestGraph first = await SeedGraphAsync(OrganizationRole.Owner, "first");
        TestGraph second = await SeedGraphAsync(OrganizationRole.Owner, "second");
        OrganizationInvitation firstOlder = CreateInvitation(
            first,
            "same@example.test",
            OrganizationRole.Member,
            tokenIssuedAt: Now.AddMinutes(-3),
            expiresAt: Now.AddDays(1));
        OrganizationInvitation firstNewer = CreateInvitation(
            first,
            "newer@example.test",
            OrganizationRole.Administrator,
            tokenIssuedAt: Now.AddDays(-2),
            expiresAt: Now.AddDays(-1));
        OrganizationInvitation secondSameEmail = CreateInvitation(
            second,
            "same@example.test",
            OrganizationRole.Member,
            tokenIssuedAt: Now.AddMinutes(-1),
            expiresAt: Now.AddDays(1));
        await SeedAsync(firstOlder, firstNewer, secondSameEmail);
        var commandCounter = new CommandCounterInterceptor();
        var options = new DbContextOptionsBuilder<EnmaDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .AddInterceptors(commandCounter)
            .Options;
        await using var dbContext = new EnmaDbContext(options);
        var queries = new OrganizationInvitationReadQueries(dbContext);

        OrganizationInvitationPage page = await queries.ListAsync(new(
            first.Organization.Id,
            Now,
            PageNumber: 1,
            PageSize: 1));

        Assert.Equal(2, page.TotalCount);
        OrganizationInvitationReadModel item = Assert.Single(page.Items);
        Assert.Equal(firstOlder.Id, item.Id);
        Assert.Equal(OrganizationInvitationState.Pending, item.Status);
        Assert.DoesNotContain(
            page.Items,
            candidate => candidate.Id == secondSameEmail.Id);

        OrganizationInvitationPage secondPage = await queries.ListAsync(new(
            first.Organization.Id,
            Now,
            PageNumber: 2,
            PageSize: 1));
        Assert.Equal(firstNewer.Id, Assert.Single(secondPage.Items).Id);
        Assert.Equal(
            OrganizationInvitationState.Expired,
            Assert.Single(secondPage.Items).Status);
        Assert.Equal(4, commandCounter.ReaderCount);
    }

    private OrganizationInvitationMutationPersistence CreatePersistence()
    {
        var options = new DbContextOptionsBuilder<EnmaDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .Options;

        return new OrganizationInvitationMutationPersistence(
            options,
            new FixedTimeProvider(Now),
            new CryptographicOrganizationInvitationTokenService());
    }

    private static CreateOrganizationInvitationPersistenceRequest CreateRequest(
        TestGraph graph,
        string email)
    {
        return new CreateOrganizationInvitationPersistenceRequest(
            graph.Actor.Id,
            graph.Organization.Id,
            graph.Membership.Id,
            email,
            OrganizationRole.Member);
    }

    private async Task<TestGraph> SeedGraphAsync(
        OrganizationRole actorRole,
        string discriminator = "default")
    {
        string suffix = $"{discriminator}-{Guid.NewGuid():N}";
        var organization = new Organization(
            $"Invitation {discriminator}",
            $"invitation-{suffix}",
            CreatedAt);
        var actor = new User(
            $"Actor {discriminator}",
            $"actor-{suffix}@example.test",
            CreatedAt);
        var membership = new OrganizationMembership(
            organization.Id,
            actor.Id,
            actorRole,
            CreatedAt);
        await SeedAsync(organization, actor, membership);
        return new TestGraph(organization, actor, membership);
    }

    private static OrganizationInvitation CreateInvitation(
        TestGraph graph,
        string email,
        OrganizationRole role,
        DateTimeOffset tokenIssuedAt,
        DateTimeOffset expiresAt)
    {
        return new OrganizationInvitation(
            graph.Organization.Id,
            email,
            role,
            graph.Membership.Id,
            new OrganizationInvitationTokenHash(
                System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
            tokenIssuedAt < CreatedAt ? tokenIssuedAt : CreatedAt,
            tokenIssuedAt,
            expiresAt);
    }

    private async Task SeedAsync(params object[] entities)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.AddRange(entities);
        await dbContext.SaveChangesAsync();
    }

    private sealed record TestGraph(
        Organization Organization,
        User Actor,
        OrganizationMembership Membership);

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class CommandCounterInterceptor : DbCommandInterceptor
    {
        public int ReaderCount { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>>
            ReaderExecutingAsync(
                DbCommand command,
                CommandEventData eventData,
                InterceptionResult<DbDataReader> result,
                CancellationToken cancellationToken = default)
        {
            ReaderCount++;
            return ValueTask.FromResult(result);
        }
    }
}
