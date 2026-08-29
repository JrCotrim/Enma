using Enma.Application.Authorization;
using Enma.Application.Deadlines;
using Enma.Application.Deadlines.Complete;
using Enma.Application.Deadlines.Reopen;
using Enma.Application.Deadlines.Update;
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
public sealed class LegalDeadlineMutationUseCasesPersistenceTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset CreatedAt = new(
        2026, 8, 13, 20, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset CompletedAt = CreatedAt.AddHours(2);
    private static readonly DateOnly OriginalDueDate = new(2026, 9, 1);

    public Task InitializeAsync()
    {
        return fixture.ResetDatabaseAsync();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Mutations_WithLiveDualMembership_UseContextualCurrentRole()
    {
        Organization organizationA = CreateOrganization(
            "Mutation Organization A",
            "use-case-mutation-organization-a");
        Organization organizationB = CreateOrganization(
            "Mutation Organization B",
            "use-case-mutation-organization-b");
        User user = new(
            "Mutation User",
            "deadline-mutation@example.test",
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
        var deadlineA = new LegalDeadline(
            organizationA.Id,
            processA.Id,
            "Deadline A",
            OriginalDueDate,
            CreatedAt);
        var deadlineB = new LegalDeadline(
            organizationB.Id,
            processB.Id,
            "Deadline B",
            OriginalDueDate,
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
            processB,
            deadlineA,
            deadlineB);

        await using EnmaDbContext authorizationContext = fixture.CreateDbContext();
        DeadlineActionAuthorization authorization = CreateAuthorization(
            authorizationContext);
        LegalDeadlineMutationPersistence persistence = CreatePersistence();
        var update = new UpdateLegalDeadlineUseCase(authorization, persistence);
        var complete = new CompleteLegalDeadlineUseCase(
            authorization,
            persistence,
            new FixedTimeProvider(CompletedAt));
        var reopen = new ReopenLegalDeadlineUseCase(authorization, persistence);

        UpdateLegalDeadlineResult memberUpdate = await update.ExecuteAsync(
            user.Id,
            organizationA.Id,
            deadlineA.Id,
            "Denied A",
            new DateOnly(2026, 10, 1));
        CompleteLegalDeadlineResult memberComplete = await complete.ExecuteAsync(
            user.Id,
            organizationA.Id,
            deadlineA.Id);
        UpdateLegalDeadlineResult ownerUpdate = await update.ExecuteAsync(
            user.Id,
            organizationB.Id,
            deadlineB.Id,
            "Updated B",
            new DateOnly(2026, 10, 2));
        CompleteLegalDeadlineResult ownerComplete = await complete.ExecuteAsync(
            user.Id,
            organizationB.Id,
            deadlineB.Id);

        Assert.Equal(UpdateLegalDeadlineResultStatus.AccessDenied, memberUpdate.Status);
        Assert.Equal(
            CompleteLegalDeadlineResultStatus.AccessDenied,
            memberComplete.Status);
        Assert.Equal(UpdateLegalDeadlineResultStatus.Updated, ownerUpdate.Status);
        Assert.Equal(
            CompleteLegalDeadlineResultStatus.Succeeded,
            ownerComplete.Status);

        await ChangeRoleAsync(
            user.Id,
            organizationA.Id,
            OrganizationRole.Administrator);

        UpdateLegalDeadlineResult administratorUpdate = await update.ExecuteAsync(
            user.Id,
            organizationA.Id,
            deadlineA.Id,
            "Updated A",
            new DateOnly(2026, 10, 3));
        CompleteLegalDeadlineResult administratorComplete =
            await complete.ExecuteAsync(
                user.Id,
                organizationA.Id,
                deadlineA.Id);
        UpdateLegalDeadlineResult crossTenant = await update.ExecuteAsync(
            user.Id,
            organizationB.Id,
            deadlineA.Id,
            "Cross tenant",
            new DateOnly(2026, 10, 4));

        Assert.Equal(
            UpdateLegalDeadlineResultStatus.Updated,
            administratorUpdate.Status);
        Assert.Equal(
            CompleteLegalDeadlineResultStatus.Succeeded,
            administratorComplete.Status);
        Assert.Equal(UpdateLegalDeadlineResultStatus.NotFound, crossTenant.Status);

        await ChangeRoleAsync(
            user.Id,
            organizationA.Id,
            OrganizationRole.Member);

        ReopenLegalDeadlineResult demotedReopen = await reopen.ExecuteAsync(
            user.Id,
            organizationA.Id,
            deadlineA.Id);

        Assert.Equal(
            ReopenLegalDeadlineResultStatus.AccessDenied,
            demotedReopen.Status);

        await using EnmaDbContext verificationContext = fixture.CreateDbContext();
        LegalDeadline persistedA = await verificationContext.LegalDeadlines
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == deadlineA.Id);
        LegalDeadline persistedB = await verificationContext.LegalDeadlines
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == deadlineB.Id);
        Assert.Equal("Updated A", persistedA.Title);
        Assert.Equal(new DateOnly(2026, 10, 3), persistedA.DueDate);
        Assert.Equal(CompletedAt, persistedA.CompletedAt);
        Assert.Equal(organizationA.Id, persistedA.OrganizationId);
        Assert.Equal(processA.Id, persistedA.ProcessId);
        Assert.Equal("Updated B", persistedB.Title);
        Assert.Equal(CompletedAt, persistedB.CompletedAt);
        Assert.Equal(organizationB.Id, persistedB.OrganizationId);
        Assert.Equal(processB.Id, persistedB.ProcessId);
    }

    [Fact]
    public async Task CompleteUpdateReopenUpdate_EnforcesOfficialLifecycleWorkflow()
    {
        Organization organization = CreateOrganization(
            "Workflow Organization",
            "deadline-workflow-organization");
        User owner = new("Workflow Owner", "workflow@example.test", CreatedAt);
        var membership = new OrganizationMembership(
            organization.Id,
            owner.Id,
            OrganizationRole.Owner,
            CreatedAt);
        var client = new Client(organization.Id, "Workflow Client", CreatedAt);
        var process = new LegalProcess(
            organization.Id,
            client.Id,
            "Workflow Process",
            CreatedAt);
        var deadline = new LegalDeadline(
            organization.Id,
            process.Id,
            "Original title",
            OriginalDueDate,
            CreatedAt);
        await SeedAsync(
            organization,
            owner,
            membership,
            client,
            process,
            deadline);

        await using EnmaDbContext authorizationContext = fixture.CreateDbContext();
        DeadlineActionAuthorization authorization = CreateAuthorization(
            authorizationContext);
        LegalDeadlineMutationPersistence persistence = CreatePersistence();
        var complete = new CompleteLegalDeadlineUseCase(
            authorization,
            persistence,
            new FixedTimeProvider(CompletedAt));
        var update = new UpdateLegalDeadlineUseCase(authorization, persistence);
        var reopen = new ReopenLegalDeadlineUseCase(authorization, persistence);

        CompleteLegalDeadlineResult completeResult = await complete.ExecuteAsync(
            owner.Id,
            organization.Id,
            deadline.Id);
        UpdateLegalDeadlineResult conflictingUpdate = await update.ExecuteAsync(
            owner.Id,
            organization.Id,
            deadline.Id,
            "Forbidden title",
            new DateOnly(2027, 1, 1));

        Assert.Equal(
            CompleteLegalDeadlineResultStatus.Succeeded,
            completeResult.Status);
        Assert.Equal(UpdateLegalDeadlineResultStatus.Conflict, conflictingUpdate.Status);

        LegalDeadline afterConflict = await FindDeadlineAsync(deadline.Id);
        Assert.Equal("Original title", afterConflict.Title);
        Assert.Equal(OriginalDueDate, afterConflict.DueDate);
        Assert.Equal(CompletedAt, afterConflict.CompletedAt);

        ReopenLegalDeadlineResult reopenResult = await reopen.ExecuteAsync(
            owner.Id,
            organization.Id,
            deadline.Id);
        DateOnly replacementDate = new(2027, 2, 28);
        UpdateLegalDeadlineResult updateResult = await update.ExecuteAsync(
            owner.Id,
            organization.Id,
            deadline.Id,
            "  Reopened title  ",
            replacementDate);

        Assert.Equal(ReopenLegalDeadlineResultStatus.Succeeded, reopenResult.Status);
        Assert.Equal(UpdateLegalDeadlineResultStatus.Updated, updateResult.Status);

        LegalDeadline persisted = await FindDeadlineAsync(deadline.Id);
        Assert.Equal("Reopened title", persisted.Title);
        Assert.Equal(replacementDate, persisted.DueDate);
        Assert.Null(persisted.CompletedAt);
        Assert.Equal(organization.Id, persisted.OrganizationId);
        Assert.Equal(process.Id, persisted.ProcessId);
    }

    [Fact]
    public async Task CompleteAsync_OrganizationDeactivatedAfterInitialAuthorization_DeniesLive()
    {
        Organization organization = CreateOrganization(
            "Stale lifecycle organization",
            "stale-lifecycle-organization");
        var user = new User(
            "Stale lifecycle actor",
            "stale-lifecycle@example.test",
            CreatedAt);
        var membership = new OrganizationMembership(
            organization.Id,
            user.Id,
            OrganizationRole.Owner,
            CreatedAt);
        var client = new Client(organization.Id, "Stale lifecycle client", CreatedAt);
        var process = new LegalProcess(
            organization.Id,
            client.Id,
            "Stale lifecycle process",
            CreatedAt);
        var deadline = new LegalDeadline(
            organization.Id,
            process.Id,
            "Stale lifecycle deadline",
            OriginalDueDate,
            CreatedAt);
        await SeedAsync(
            organization,
            user,
            membership,
            client,
            process,
            deadline);
        await using EnmaDbContext authorizationContext = fixture.CreateDbContext();
        var persistence = new BeforeDeadlineMutationPersistence(
            CreatePersistence(),
            () => DeactivateOrganizationAsync(organization.Id));
        var useCase = new CompleteLegalDeadlineUseCase(
            CreateAuthorization(authorizationContext),
            persistence,
            new FixedTimeProvider(CompletedAt));

        CompleteLegalDeadlineResult result = await useCase.ExecuteAsync(
            user.Id,
            organization.Id,
            deadline.Id);

        Assert.Same(CompleteLegalDeadlineResult.AccessDenied, result);
        Assert.Null((await FindDeadlineAsync(deadline.Id)).CompletedAt);
        await using EnmaDbContext verificationContext = fixture.CreateDbContext();
        Assert.False(await verificationContext.AuditLogs.AnyAsync());
    }

    private DeadlineActionAuthorization CreateAuthorization(
        EnmaDbContext dbContext)
    {
        return new DeadlineActionAuthorization(
            new OrganizationAccessAuthorization(
                new OrganizationAccessLookup(dbContext)));
    }

    private LegalDeadlineMutationPersistence CreatePersistence()
    {
        DbContextOptions<EnmaDbContext> options =
            new DbContextOptionsBuilder<EnmaDbContext>()
                .UseNpgsql(fixture.ConnectionString)
                .Options;
        return new LegalDeadlineMutationPersistence(
            options,
            new FixedTimeProvider(CompletedAt));
    }

    private async Task ChangeRoleAsync(
        Guid userId,
        Guid organizationId,
        OrganizationRole role)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        OrganizationMembership membership = await dbContext.OrganizationMemberships
            .SingleAsync(candidate =>
                candidate.UserId == userId &&
                candidate.OrganizationId == organizationId);
        membership.ChangeRole(role);
        await dbContext.SaveChangesAsync();
    }

    private async Task DeactivateOrganizationAsync(Guid organizationId)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Organization organization = await dbContext.Organizations.SingleAsync(
            candidate => candidate.Id == organizationId);
        organization.Deactivate();
        await dbContext.SaveChangesAsync();
    }

    private async Task<LegalDeadline> FindDeadlineAsync(Guid deadlineId)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        return await dbContext.LegalDeadlines
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == deadlineId);
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

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return utcNow;
        }
    }

    private sealed class BeforeDeadlineMutationPersistence(
        ILegalDeadlineMutationPersistence inner,
        Func<Task> before) : ILegalDeadlineMutationPersistence
    {
        public Task<LegalDeadlineDetailsMutationPersistenceResult>
            UpdateDetailsAsync(
                LegalDeadlineMutationPersistenceRequest request,
                Func<LegalDeadlineMutationLockedState,
                    LegalDeadlineMutationDecision> decide,
                CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public async Task<LegalDeadlineLifecycleMutationPersistenceResult>
            CompleteAsync(
                LegalDeadlineMutationPersistenceRequest request,
                Func<LegalDeadlineMutationLockedState,
                    LegalDeadlineMutationDecision> decide,
                CancellationToken cancellationToken = default)
        {
            await before();
            return await inner.CompleteAsync(request, decide, cancellationToken);
        }

        public Task<LegalDeadlineLifecycleMutationPersistenceResult> ReopenAsync(
            LegalDeadlineMutationPersistenceRequest request,
            Func<LegalDeadlineMutationLockedState, LegalDeadlineMutationDecision> decide,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
