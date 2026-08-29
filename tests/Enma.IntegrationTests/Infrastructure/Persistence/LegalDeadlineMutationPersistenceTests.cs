using Enma.Application.Deadlines;
using Enma.Domain.Clients;
using Enma.Domain.Deadlines;
using Enma.Domain.Organizations;
using Enma.Domain.Processes;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Enma.IntegrationTests.Infrastructure.Persistence;

[Collection(PostgreSqlCollection.Name)]
public sealed class LegalDeadlineMutationPersistenceTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private readonly Dictionary<Guid, (Guid UserId, Guid MembershipId)> _actors = [];

    private static readonly DateTimeOffset CreatedAt = new(
        2026, 8, 13, 20, 0, 0, TimeSpan.Zero);
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
    public async Task UpdateDetailsAsync_WithMatchingTenant_PersistsDateOnlyAndPreservesImmutableFields()
    {
        (Organization organization, Client client, LegalProcess process,
            LegalDeadline deadline) = await SeedDeadlineAsync();
        DateOnly updatedDueDate = new(2027, 2, 28);

        LegalDeadlineDetailsMutationPersistenceResult result =
            await UpdateDetailsAsync(
                CreatePersistence(),
                deadline.Id,
                organization.Id,
                "  Updated title  ",
                updatedDueDate);

        Assert.Equal(LegalDeadlineDetailsMutationPersistenceResult.Updated, result);

        LegalDeadline persisted = await FindDeadlineAsync(deadline.Id);
        Assert.Equal("Updated title", persisted.Title);
        Assert.Equal(updatedDueDate, persisted.DueDate);
        Assert.Equal(organization.Id, persisted.OrganizationId);
        Assert.Equal(process.Id, persisted.ProcessId);
        Assert.Equal(CreatedAt, persisted.CreatedAt);
        Assert.Null(persisted.CompletedAt);
        Assert.Equal(organization.Id, client.OrganizationId);
    }

    [Fact]
    public async Task UpdateDetailsAsync_WithMissingAndCrossTenantDeadline_ReturnsSameResultWithoutMutation()
    {
        Organization organizationA = CreateOrganization(
            "Mutation Organization A",
            "deadline-mutation-organization-a");
        Organization organizationB = CreateOrganization(
            "Mutation Organization B",
            "deadline-mutation-organization-b");
        var clientB = new Client(organizationB.Id, "Client B", CreatedAt);
        var processB = new LegalProcess(
            organizationB.Id,
            clientB.Id,
            "Process B",
            CreatedAt);
        var deadlineB = new LegalDeadline(
            organizationB.Id,
            processB.Id,
            "Protected deadline",
            OriginalDueDate,
            CreatedAt);
        await SeedAsync(organizationA, organizationB, clientB, processB, deadlineB);
        LegalDeadlineMutationPersistence persistence = CreatePersistence();

        LegalDeadlineDetailsMutationPersistenceResult missing =
            await UpdateDetailsAsync(
                persistence,
                Guid.NewGuid(),
                organizationA.Id,
                "Missing update",
                new DateOnly(2027, 1, 1));
        LegalDeadlineDetailsMutationPersistenceResult crossTenant =
            await UpdateDetailsAsync(
                persistence,
                deadlineB.Id,
                organizationA.Id,
                "Cross-tenant update",
                new DateOnly(2027, 1, 1));

        Assert.Equal(LegalDeadlineDetailsMutationPersistenceResult.NotFound, missing);
        Assert.Equal(missing, crossTenant);

        LegalDeadline persisted = await FindDeadlineAsync(deadlineB.Id);
        Assert.Equal("Protected deadline", persisted.Title);
        Assert.Equal(OriginalDueDate, persisted.DueDate);
        Assert.Equal(organizationB.Id, persisted.OrganizationId);
        Assert.Equal(processB.Id, persisted.ProcessId);
    }

    [Fact]
    public async Task UpdateDetailsAsync_WhenCompleted_ReturnsConflictWithoutMutation()
    {
        (Organization organization, _, _, LegalDeadline deadline) =
            await SeedDeadlineAsync(completedAt: CreatedAt.AddHours(1));

        LegalDeadlineDetailsMutationPersistenceResult result =
            await UpdateDetailsAsync(
                CreatePersistence(),
                deadline.Id,
                organization.Id,
                "Forbidden update",
                new DateOnly(2027, 1, 1));

        Assert.Equal(LegalDeadlineDetailsMutationPersistenceResult.Conflict, result);

        LegalDeadline persisted = await FindDeadlineAsync(deadline.Id);
        Assert.Equal("Initial title", persisted.Title);
        Assert.Equal(OriginalDueDate, persisted.DueDate);
        Assert.Equal(CreatedAt.AddHours(1), persisted.CompletedAt);
        await using EnmaDbContext auditContext = fixture.CreateDbContext();
        Assert.False(await auditContext.AuditLogs.AnyAsync());
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("Valid title", true)]
    public async Task UpdateDetailsAsync_WithInvalidInput_RollsBackAtomically(
        string title,
        bool invalidDueDate)
    {
        (Organization organization, _, _, LegalDeadline deadline) =
            await SeedDeadlineAsync();

        Exception? thrownException = await Record.ExceptionAsync(
            () => UpdateDetailsAsync(
                CreatePersistence(),
                deadline.Id,
                organization.Id,
                title,
                invalidDueDate ? DateOnly.MinValue : new DateOnly(2027, 1, 1)));
        ArgumentException exception = Assert.IsAssignableFrom<ArgumentException>(
            thrownException);

        Assert.Equal(invalidDueDate ? "dueDate" : "title", exception.ParamName);
        LegalDeadline persisted = await FindDeadlineAsync(deadline.Id);
        Assert.Equal("Initial title", persisted.Title);
        Assert.Equal(OriginalDueDate, persisted.DueDate);
        Assert.Null(persisted.CompletedAt);
    }

    [Fact]
    public async Task CompleteAsync_WhenRepeated_PreservesFirstCompletionTimestamp()
    {
        (Organization organization, _, _, LegalDeadline deadline) =
            await SeedDeadlineAsync();
        DateTimeOffset firstCompletion = CreatedAt.AddHours(1);

        LegalDeadlineLifecycleMutationPersistenceResult firstResult =
            await CompleteAsync(
                CreatePersistence(),
                deadline.Id,
                organization.Id,
                firstCompletion);
        LegalDeadlineLifecycleMutationPersistenceResult secondResult =
            await CompleteAsync(
                CreatePersistence(),
                deadline.Id,
                organization.Id,
                CreatedAt.AddHours(2));

        Assert.Equal(
            LegalDeadlineLifecycleMutationPersistenceResult.Succeeded,
            firstResult);
        Assert.Equal(firstResult, secondResult);
        Assert.Equal(firstCompletion, (await FindDeadlineAsync(deadline.Id)).CompletedAt);
    }

    [Fact]
    public async Task ReopenAsync_WhenRepeated_RemainsSucceededAndPending()
    {
        (Organization organization, _, _, LegalDeadline deadline) =
            await SeedDeadlineAsync(completedAt: CreatedAt.AddHours(1));

        LegalDeadlineLifecycleMutationPersistenceResult firstResult =
            await ReopenAsync(CreatePersistence(), deadline.Id, organization.Id);
        LegalDeadlineLifecycleMutationPersistenceResult secondResult =
            await ReopenAsync(CreatePersistence(), deadline.Id, organization.Id);

        Assert.Equal(
            LegalDeadlineLifecycleMutationPersistenceResult.Succeeded,
            firstResult);
        Assert.Equal(firstResult, secondResult);
        Assert.Null((await FindDeadlineAsync(deadline.Id)).CompletedAt);
    }

    [Fact]
    public async Task CompleteAsync_WithTimestampBeforeCreation_RollsBackInvariantFailure()
    {
        (Organization organization, _, _, LegalDeadline deadline) =
            await SeedDeadlineAsync();

        ArgumentOutOfRangeException exception =
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                CompleteAsync(
                    CreatePersistence(),
                    deadline.Id,
                    organization.Id,
                    CreatedAt.AddTicks(-1)));

        Assert.Equal("completedAt", exception.ParamName);
        Assert.Null((await FindDeadlineAsync(deadline.Id)).CompletedAt);
    }

    private LegalDeadlineMutationPersistence CreatePersistence()
    {
        DbContextOptions<EnmaDbContext> options =
            new DbContextOptionsBuilder<EnmaDbContext>()
                .UseNpgsql(fixture.ConnectionString)
                .Options;
        return new LegalDeadlineMutationPersistence(options, TimeProvider.System);
    }

    private Task<LegalDeadlineDetailsMutationPersistenceResult> UpdateDetailsAsync(
        LegalDeadlineMutationPersistence persistence,
        Guid deadlineId,
        Guid organizationId,
        string title,
        DateOnly dueDate,
        CancellationToken cancellationToken = default)
    {
        return persistence.UpdateDetailsAsync(
            CreateRequest(organizationId, deadlineId),
            state =>
            {
                if (state.LegalDeadline.CompletedAt is not null)
                {
                    return LegalDeadlineMutationDecision.Conflict;
                }

                state.LegalDeadline.ChangeDetails(title, dueDate);
                return LegalDeadlineMutationDecision.Persist;
            },
            cancellationToken);
    }

    private Task<LegalDeadlineLifecycleMutationPersistenceResult> CompleteAsync(
        LegalDeadlineMutationPersistence persistence,
        Guid deadlineId,
        Guid organizationId,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken = default)
    {
        return persistence.CompleteAsync(
            CreateRequest(organizationId, deadlineId),
            state =>
            {
                state.LegalDeadline.Complete(completedAt);
                return LegalDeadlineMutationDecision.Persist;
            },
            cancellationToken);
    }

    private Task<LegalDeadlineLifecycleMutationPersistenceResult> ReopenAsync(
        LegalDeadlineMutationPersistence persistence,
        Guid deadlineId,
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        return persistence.ReopenAsync(
            CreateRequest(organizationId, deadlineId),
            state =>
            {
                state.LegalDeadline.Reopen();
                return LegalDeadlineMutationDecision.Persist;
            },
            cancellationToken);
    }

    private LegalDeadlineMutationPersistenceRequest CreateRequest(
        Guid organizationId,
        Guid deadlineId)
    {
        (Guid userId, Guid membershipId) = _actors[organizationId];
        return new LegalDeadlineMutationPersistenceRequest(
            userId,
            organizationId,
            membershipId,
            deadlineId);
    }

    private async Task<(Organization, Client, LegalProcess, LegalDeadline)>
        SeedDeadlineAsync(DateTimeOffset? completedAt = null)
    {
        Organization organization = CreateOrganization(
            "Deadline Mutation Organization",
            "deadline-mutation-organization");
        var client = new Client(organization.Id, "Mutation Client", CreatedAt);
        var process = new LegalProcess(
            organization.Id,
            client.Id,
            "Mutation Process",
            CreatedAt);
        var deadline = new LegalDeadline(
            organization.Id,
            process.Id,
            "Initial title",
            OriginalDueDate,
            CreatedAt);

        if (completedAt.HasValue)
        {
            deadline.Complete(completedAt.Value);
        }

        await SeedAsync(organization, client, process, deadline);
        return (organization, client, process, deadline);
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
        foreach (Organization organization in entities.OfType<Organization>())
        {
            var user = new User(
                "Deadline audit actor",
                $"deadline-{organization.Id:N}@example.test",
                CreatedAt);
            var membership = new OrganizationMembership(
                organization.Id,
                user.Id,
                OrganizationRole.Owner,
                CreatedAt);
            dbContext.AddRange(user, membership);
            _actors[organization.Id] = (user.Id, membership.Id);
        }

        await dbContext.SaveChangesAsync();
    }

    private static Organization CreateOrganization(string name, string slug)
    {
        return new Organization(name, slug, CreatedAt);
    }
}
