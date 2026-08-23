using System.Data;
using Enma.Application.CalendarEvents;
using Enma.Domain.CalendarEvents;
using Enma.Domain.Clients;
using Enma.Domain.Organizations;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Enma.IntegrationTests.Infrastructure.Persistence;

[Collection(PostgreSqlCollection.Name)]
public sealed class CalendarEventPersistenceConcurrencyTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        8,
        23,
        12,
        0,
        0,
        TimeSpan.Zero);

    public Task InitializeAsync() => fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData(CalendarEventOperation.Create)]
    [InlineData(CalendarEventOperation.UpdateAssociation)]
    public async Task ExecuteAsync_UsesRelationIdentityOrganizationLockOrder(
        CalendarEventOperation operation)
    {
        TenantGraph graph = await SeedTenantAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using EnmaDbContext blockerContext = fixture.CreateDbContext();
        await using IDbContextTransaction blockerTransaction =
            await blockerContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                timeout.Token);
        await LockClientAsync(
            blockerContext,
            graph.Client.Id,
            graph.Organization.Id,
            timeout.Token);
        Task? calendarEventOperation = null;

        try
        {
            calendarEventOperation = operation switch
            {
                CalendarEventOperation.Create => CreateEventAsync(
                    graph,
                    timeout.Token),
                CalendarEventOperation.UpdateAssociation => UpdateEventAsync(
                    graph,
                    timeout.Token),
                _ => throw new ArgumentOutOfRangeException(nameof(operation))
            };

            await WaitForBlockedRelationLockAsync(timeout.Token);
            Assert.False(calendarEventOperation.IsCompleted);

            await LockMembershipAsync(
                blockerContext,
                graph.ActorMembership.Id,
                graph.Organization.Id,
                timeout.Token);
            await LockOrganizationAsync(
                blockerContext,
                graph.Organization.Id,
                timeout.Token);
            await blockerTransaction.CommitAsync(timeout.Token);

            await calendarEventOperation.WaitAsync(timeout.Token);
        }
        finally
        {
            if (blockerTransaction.GetDbTransaction().Connection is not null)
            {
                await blockerTransaction.RollbackAsync(CancellationToken.None);
            }

            await DrainTaskAsync(calendarEventOperation);
        }
    }

    private async Task CreateEventAsync(
        TenantGraph graph,
        CancellationToken cancellationToken)
    {
        var persistence = new CalendarEventCreationPersistence(CreateOptions());
        var request = new CalendarEventCreationPersistenceRequest(
            graph.ActorUser.Id,
            graph.Organization.Id,
            graph.ActorMembership.Id,
            graph.Client.Id,
            null,
            null);

        CalendarEventCreationPersistenceResult result =
            await persistence.ExecuteAsync(
                request,
                state =>
                {
                    Assert.True(state.IsOrganizationActive);
                    Assert.True(state.Actor?.IsMembershipActive);
                    Assert.True(state.Actor?.IsUserActive);
                    Assert.True(state.IsClientAvailable);

                    return CalendarEventCreationDecision.Persist(
                        new CalendarEvent(
                            graph.Organization.Id,
                            "Concurrent creation",
                            null,
                            CreatedAt.AddHours(1),
                            CreatedAt.AddHours(2),
                            null,
                            graph.Client.Id,
                            null,
                            null,
                            graph.ActorMembership.Id,
                            CreatedAt));
                },
                cancellationToken);

        Assert.Equal(
            CalendarEventCreationDecisionStatus.Persist,
            result.Status);
    }

    private async Task UpdateEventAsync(
        TenantGraph graph,
        CancellationToken cancellationToken)
    {
        var persistence = new CalendarEventMutationPersistence(CreateOptions());
        var request = new CalendarEventMutationPersistenceRequest(
            graph.ActorUser.Id,
            graph.Organization.Id,
            graph.ActorMembership.Id,
            graph.CalendarEvent.Id);

        CalendarEventMutationPersistenceResult result =
            await persistence.ExecuteAsync(
                request,
                state =>
                {
                    if (!state.AssociationLookupPerformed)
                    {
                        return CalendarEventMutationDecision.ValidateAssociation(
                            graph.Client.Id,
                            null);
                    }

                    Assert.Equal(graph.Client.Id, state.ValidatedClientId);
                    Assert.True(state.IsClientAvailable);
                    state.CalendarEvent.ChangeAssociation(graph.Client.Id, null);
                    return CalendarEventMutationDecision.Persist;
                },
                cancellationToken);

        Assert.Equal(CalendarEventMutationPersistenceResult.Succeeded, result);
    }

    private async Task<TenantGraph> SeedTenantAsync()
    {
        var organization = new Organization(
            "Calendar concurrency",
            $"calendar-concurrency-{Guid.NewGuid():N}",
            CreatedAt);
        var actorUser = new User(
            "Calendar actor",
            $"calendar-{Guid.NewGuid():N}@example.test",
            CreatedAt);
        var actorMembership = new OrganizationMembership(
            organization.Id,
            actorUser.Id,
            OrganizationRole.Owner,
            CreatedAt);
        var client = new Client(
            organization.Id,
            "Concurrent client",
            CreatedAt);
        var calendarEvent = new CalendarEvent(
            organization.Id,
            "Existing event",
            null,
            CreatedAt.AddHours(1),
            CreatedAt.AddHours(2),
            null,
            null,
            null,
            null,
            actorMembership.Id,
            CreatedAt);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.AddRange(
            organization,
            actorUser,
            actorMembership,
            client,
            calendarEvent);
        await dbContext.SaveChangesAsync();

        return new TenantGraph(
            organization,
            actorUser,
            actorMembership,
            client,
            calendarEvent);
    }

    private DbContextOptions<EnmaDbContext> CreateOptions()
    {
        return new DbContextOptionsBuilder<EnmaDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .Options;
    }

    private static Task<Client> LockClientAsync(
        EnmaDbContext dbContext,
        Guid clientId,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        return dbContext.Clients
            .FromSqlInterpolated(
                $"""
                SELECT * FROM clients
                WHERE id = {clientId}
                  AND organization_id = {organizationId}
                FOR UPDATE
                """)
            .SingleAsync(cancellationToken);
    }

    private static Task<OrganizationMembership> LockMembershipAsync(
        EnmaDbContext dbContext,
        Guid membershipId,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        return dbContext.OrganizationMemberships
            .FromSqlInterpolated(
                $"""
                SELECT * FROM organization_memberships
                WHERE id = {membershipId}
                  AND organization_id = {organizationId}
                FOR UPDATE
                """)
            .SingleAsync(cancellationToken);
    }

    private static Task<Organization> LockOrganizationAsync(
        EnmaDbContext dbContext,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        return dbContext.Organizations
            .FromSqlInterpolated(
                $"""
                SELECT * FROM organizations
                WHERE id = {organizationId}
                FOR UPDATE
                """)
            .SingleAsync(cancellationToken);
    }

    private async Task WaitForBlockedRelationLockAsync(
        CancellationToken cancellationToken)
    {
        const string RelationQueryPattern = "%FROM clients%";
        const string LockQueryPattern = "%FOR UPDATE%";
        await using EnmaDbContext observationContext = fixture.CreateDbContext();

        while (true)
        {
            int waitingCommandCount = await observationContext.Database
                .SqlQuery<int>(
                    $"""
                    SELECT COUNT(*)::integer AS "Value"
                    FROM pg_stat_activity
                    WHERE datname = current_database()
                      AND pid <> pg_backend_pid()
                      AND wait_event_type = 'Lock'
                      AND query ILIKE {RelationQueryPattern}
                      AND query ILIKE {LockQueryPattern}
                    """)
                .SingleAsync(cancellationToken);

            if (waitingCommandCount > 0)
            {
                return;
            }

            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static async Task DrainTaskAsync(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }

    public enum CalendarEventOperation
    {
        Create = 0,
        UpdateAssociation = 1
    }

    private sealed record TenantGraph(
        Organization Organization,
        User ActorUser,
        OrganizationMembership ActorMembership,
        Client Client,
        CalendarEvent CalendarEvent);
}
