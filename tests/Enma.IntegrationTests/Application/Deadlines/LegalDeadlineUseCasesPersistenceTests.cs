using Enma.Application.Authorization;
using Enma.Application.Deadlines;
using Enma.Application.Deadlines.Create;
using Enma.Application.Deadlines.GetById;
using Enma.Application.Deadlines.List;
using Enma.Domain.Clients;
using Enma.Domain.Deadlines;
using Enma.Domain.Organizations;
using Enma.Domain.Processes;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Enma.Infrastructure.Persistence.Queries;
using Enma.IntegrationTests.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Enma.IntegrationTests.Application.Deadlines;

[Collection(PostgreSqlCollection.Name)]
public sealed class LegalDeadlineUseCasesPersistenceTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        8,
        13,
        20,
        0,
        0,
        TimeSpan.Zero);

    private static readonly DateTimeOffset DeadlineCreatedAt =
        CreatedAt.AddHours(1);

    public Task InitializeAsync()
    {
        return fixture.ResetDatabaseAsync();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    [Fact]
    public async Task CreateGetAndList_WithLiveDualMembershipAndInactiveClient_PreserveBoundaries()
    {
        Organization organizationA = CreateOrganization(
            "Organization A",
            "organization-a");
        Organization organizationB = CreateOrganization(
            "Organization B",
            "organization-b");
        User user = new(
            "Contextual User",
            "contextual@example.test",
            CreatedAt);
        var membershipA = new OrganizationMembership(
            organizationA.Id,
            user.Id,
            OrganizationRole.Member,
            CreatedAt);
        var membershipB = new OrganizationMembership(
            organizationB.Id,
            user.Id,
            OrganizationRole.Owner,
            CreatedAt);
        var clientA = new Client(organizationA.Id, "Client A", CreatedAt);
        var clientB = new Client(organizationB.Id, "Client B", CreatedAt);
        var processA = new LegalProcess(
            organizationA.Id,
            clientA.Id,
            "Process A",
            CreatedAt);
        var processB = new LegalProcess(
            organizationB.Id,
            clientB.Id,
            "Process B",
            CreatedAt);
        await SeedAsync(
            organizationA,
            organizationB,
            user,
            membershipA,
            membershipB,
            clientA,
            clientB,
            processA,
            processB);

        await using EnmaDbContext operationContext = fixture.CreateDbContext();
        CreateLegalDeadlineUseCase createUseCase = CreateCreateUseCase(
            operationContext);
        GetLegalDeadlineUseCase getUseCase = CreateGetUseCase(operationContext);
        ListLegalDeadlinesUseCase listUseCase = CreateListUseCase(
            operationContext);

        CreateLegalDeadlineResult memberCreateA = await createUseCase.ExecuteAsync(
            user.Id,
            organizationA.Id,
            processA.Id,
            "Denied Member Deadline",
            new DateOnly(2026, 9, 1));
        CreateLegalDeadlineResult ownerCreateB = await createUseCase.ExecuteAsync(
            user.Id,
            organizationB.Id,
            processB.Id,
            "  Organization B Deadline  ",
            new DateOnly(2026, 9, 2));

        Assert.Equal(
            CreateLegalDeadlineResultStatus.AccessDenied,
            memberCreateA.Status);
        Assert.Equal(
            CreateLegalDeadlineResultStatus.Created,
            ownerCreateB.Status);

        await ChangeRoleAsync(
            user.Id,
            organizationA.Id,
            OrganizationRole.Administrator);

        CreateLegalDeadlineResult administratorCreateA =
            await createUseCase.ExecuteAsync(
                user.Id,
                organizationA.Id,
                processA.Id,
                "  Organization A Deadline  ",
                new DateOnly(2026, 9, 1));

        Assert.Equal(
            CreateLegalDeadlineResultStatus.Created,
            administratorCreateA.Status);

        await ChangeRoleAsync(
            user.Id,
            organizationA.Id,
            OrganizationRole.Member);

        CreateLegalDeadlineResult demotedCreateA = await createUseCase.ExecuteAsync(
            user.Id,
            organizationA.Id,
            processA.Id,
            "Denied Demoted Deadline",
            new DateOnly(2026, 9, 3));

        Assert.Equal(
            CreateLegalDeadlineResultStatus.AccessDenied,
            demotedCreateA.Status);

        Guid deadlineAId = AssertDeadlineId(administratorCreateA);
        Guid deadlineBId = AssertDeadlineId(ownerCreateB);
        await using (EnmaDbContext verificationContext = fixture.CreateDbContext())
        {
            LegalDeadline persistedA = await verificationContext.LegalDeadlines
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Id == deadlineAId);
            LegalDeadline persistedB = await verificationContext.LegalDeadlines
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Id == deadlineBId);

            Assert.Equal(organizationA.Id, persistedA.OrganizationId);
            Assert.Equal(processA.Id, persistedA.ProcessId);
            Assert.Equal("Organization A Deadline", persistedA.Title);
            Assert.Equal(new DateOnly(2026, 9, 1), persistedA.DueDate);
            Assert.Equal(DeadlineCreatedAt, persistedA.CreatedAt);
            Assert.Null(persistedA.CompletedAt);
            Assert.Equal(organizationB.Id, persistedB.OrganizationId);
            Assert.Equal(processB.Id, persistedB.ProcessId);
        }

        GetLegalDeadlineResult crossTenantGet = await getUseCase.ExecuteAsync(
            user.Id,
            organizationA.Id,
            deadlineBId);
        GetLegalDeadlineResult getA = await getUseCase.ExecuteAsync(
            user.Id,
            organizationA.Id,
            deadlineAId);
        GetLegalDeadlineResult getB = await getUseCase.ExecuteAsync(
            user.Id,
            organizationB.Id,
            deadlineBId);

        Assert.Equal(GetLegalDeadlineResultStatus.NotFound, crossTenantGet.Status);
        Assert.Equal(GetLegalDeadlineResultStatus.Succeeded, getA.Status);
        Assert.Equal(GetLegalDeadlineResultStatus.Succeeded, getB.Status);

        await CompleteDeadlineAndDeactivateClientAsync(deadlineAId, clientA.Id);

        GetLegalDeadlineResult inactiveClientGet = await getUseCase.ExecuteAsync(
            user.Id,
            organizationA.Id,
            deadlineAId);
        ListLegalDeadlinesResult listA = await listUseCase.ExecuteAsync(
            user.Id,
            organizationA.Id);
        ListLegalDeadlinesResult listB = await listUseCase.ExecuteAsync(
            user.Id,
            organizationB.Id);

        Assert.Equal(
            GetLegalDeadlineResultStatus.Succeeded,
            inactiveClientGet.Status);
        Assert.Equal(clientA.Name, inactiveClientGet.LegalDeadline?.ClientName);
        Assert.Equal(
            Enma.Application.Deadlines.LegalDeadlineReadState.Completed,
            inactiveClientGet.LegalDeadline?.State);
        Assert.Collection(
            listA.Items,
            item =>
            {
                Assert.Equal(deadlineAId, item.Id);
                Assert.Equal(processA.Title, item.ProcessTitle);
                Assert.Equal(clientA.Name, item.ClientName);
                Assert.Equal(
                    Enma.Application.Deadlines.LegalDeadlineReadState.Completed,
                    item.State);
            });
        Assert.Collection(
            listB.Items,
            item =>
            {
                Assert.Equal(deadlineBId, item.Id);
                Assert.Equal(processB.Title, item.ProcessTitle);
                Assert.Equal(clientB.Name, item.ClientName);
                Assert.Equal(
                    Enma.Application.Deadlines.LegalDeadlineReadState.Pending,
                    item.State);
            });
    }

    [Fact]
    public async Task CreateAsync_WithMissingAndCrossTenantProcesses_ReturnsSameResultWithoutPersistence()
    {
        Organization organizationA = CreateOrganization(
            "Organization A",
            "organization-a");
        Organization organizationB = CreateOrganization(
            "Organization B",
            "organization-b");
        User owner = new("Owner", "owner@example.test", CreatedAt);
        var membership = new OrganizationMembership(
            organizationA.Id,
            owner.Id,
            OrganizationRole.Owner,
            CreatedAt);
        var clientB = new Client(organizationB.Id, "Client B", CreatedAt);
        var processB = new LegalProcess(
            organizationB.Id,
            clientB.Id,
            "Process B",
            CreatedAt);
        await SeedAsync(
            organizationA,
            organizationB,
            owner,
            membership,
            clientB,
            processB);

        await using EnmaDbContext operationContext = fixture.CreateDbContext();
        CreateLegalDeadlineUseCase useCase = CreateCreateUseCase(operationContext);

        CreateLegalDeadlineResult missing = await useCase.ExecuteAsync(
            owner.Id,
            organizationA.Id,
            Guid.NewGuid(),
            "Missing Process Deadline",
            new DateOnly(2026, 9, 1));
        CreateLegalDeadlineResult crossTenant = await useCase.ExecuteAsync(
            owner.Id,
            organizationA.Id,
            processB.Id,
            "Cross-tenant Process Deadline",
            new DateOnly(2026, 9, 1));

        Assert.Same(CreateLegalDeadlineResult.RelatedProcessUnavailable, missing);
        Assert.Same(missing, crossTenant);
        Assert.False(await operationContext.LegalDeadlines.AnyAsync());
        Assert.False(await operationContext.AuditLogs.AnyAsync());
    }

    [Fact]
    public async Task CreateAsync_UserDeactivatedAfterInitialAuthorization_DeniesLive()
    {
        Organization organization = CreateOrganization(
            "Stale deadline organization",
            "stale-deadline-organization");
        var user = new User(
            "Stale deadline actor",
            "stale-deadline@example.test",
            CreatedAt);
        var membership = new OrganizationMembership(
            organization.Id,
            user.Id,
            OrganizationRole.Owner,
            CreatedAt);
        var client = new Client(organization.Id, "Stale deadline client", CreatedAt);
        var process = new LegalProcess(
            organization.Id,
            client.Id,
            "Stale deadline process",
            CreatedAt);
        await SeedAsync(organization, user, membership, client, process);
        await using EnmaDbContext operationContext = fixture.CreateDbContext();
        var timeProvider = new FixedTimeProvider(DeadlineCreatedAt);
        DbContextOptions<EnmaDbContext> options =
            new DbContextOptionsBuilder<EnmaDbContext>()
                .UseNpgsql(fixture.ConnectionString)
                .Options;
        var persistence = new BeforeDeadlineCreationPersistence(
            new LegalDeadlineCreationPersistence(options, timeProvider),
            () => DeactivateUserAsync(user.Id));
        var useCase = new CreateLegalDeadlineUseCase(
            CreateActionAuthorization(operationContext),
            new ProcessOrganizationOwnershipLookup(operationContext),
            persistence,
            timeProvider);

        CreateLegalDeadlineResult result = await useCase.ExecuteAsync(
            user.Id,
            organization.Id,
            process.Id,
            "Must not persist",
            new DateOnly(2026, 10, 1));

        Assert.Same(CreateLegalDeadlineResult.AccessDenied, result);
        await using EnmaDbContext verificationContext = fixture.CreateDbContext();
        Assert.False(await verificationContext.LegalDeadlines.AnyAsync());
        Assert.False(await verificationContext.AuditLogs.AnyAsync());
    }

    private CreateLegalDeadlineUseCase CreateCreateUseCase(
        EnmaDbContext dbContext)
    {
        var timeProvider = new FixedTimeProvider(DeadlineCreatedAt);
        DbContextOptions<EnmaDbContext> options =
            new DbContextOptionsBuilder<EnmaDbContext>()
                .UseNpgsql(fixture.ConnectionString)
                .Options;
        return new CreateLegalDeadlineUseCase(
            CreateActionAuthorization(dbContext),
            new ProcessOrganizationOwnershipLookup(dbContext),
            new LegalDeadlineCreationPersistence(options, timeProvider),
            timeProvider);
    }

    private static GetLegalDeadlineUseCase CreateGetUseCase(
        EnmaDbContext dbContext)
    {
        return new GetLegalDeadlineUseCase(
            CreateActionAuthorization(dbContext),
            new LegalDeadlineReadQueries(dbContext));
    }

    private static ListLegalDeadlinesUseCase CreateListUseCase(
        EnmaDbContext dbContext)
    {
        return new ListLegalDeadlinesUseCase(
            CreateActionAuthorization(dbContext),
            new LegalDeadlineReadQueries(dbContext));
    }

    private static DeadlineActionAuthorization CreateActionAuthorization(
        EnmaDbContext dbContext)
    {
        return new DeadlineActionAuthorization(
            new OrganizationAccessAuthorization(
                new OrganizationAccessLookup(dbContext)));
    }

    private async Task ChangeRoleAsync(
        Guid userId,
        Guid organizationId,
        OrganizationRole role)
    {
        await using EnmaDbContext mutationContext = fixture.CreateDbContext();
        OrganizationMembership membership = await mutationContext
            .OrganizationMemberships
            .SingleAsync(candidate =>
                candidate.UserId == userId &&
                candidate.OrganizationId == organizationId);
        membership.ChangeRole(role);
        await mutationContext.SaveChangesAsync();
    }

    private async Task CompleteDeadlineAndDeactivateClientAsync(
        Guid deadlineId,
        Guid clientId)
    {
        await using EnmaDbContext mutationContext = fixture.CreateDbContext();
        LegalDeadline deadline = await mutationContext.LegalDeadlines.SingleAsync(
            candidate => candidate.Id == deadlineId);
        Client client = await mutationContext.Clients.SingleAsync(
            candidate => candidate.Id == clientId);
        deadline.Complete(DeadlineCreatedAt.AddDays(1));
        client.Deactivate();
        await mutationContext.SaveChangesAsync();
    }

    private async Task DeactivateUserAsync(Guid userId)
    {
        await using EnmaDbContext mutationContext = fixture.CreateDbContext();
        User user = await mutationContext.Users.SingleAsync(
            candidate => candidate.Id == userId);
        user.Deactivate();
        await mutationContext.SaveChangesAsync();
    }

    private async Task SeedAsync(params object[] entities)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.AddRange(entities);
        await dbContext.SaveChangesAsync();
    }

    private static Organization CreateOrganization(string name, string slug)
    {
        return new Organization(name, slug, CreatedAt);
    }

    private static Guid AssertDeadlineId(CreateLegalDeadlineResult result)
    {
        return Assert.IsType<Guid>(result.DeadlineId);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return utcNow;
        }
    }

    private sealed class BeforeDeadlineCreationPersistence(
        ILegalDeadlineCreationPersistence inner,
        Func<Task> before) : ILegalDeadlineCreationPersistence
    {
        public async Task<LegalDeadlineCreationPersistenceResult> ExecuteAsync(
            LegalDeadlineCreationPersistenceRequest request,
            Func<LegalDeadlineCreationLockedState, LegalDeadlineCreationDecision> decide,
            CancellationToken cancellationToken = default)
        {
            await before();
            return await inner.ExecuteAsync(request, decide, cancellationToken);
        }
    }
}
