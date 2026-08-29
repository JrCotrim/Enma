using System.Reflection;
using Enma.Application.Auditing.List;
using Enma.Domain.Auditing;
using Enma.Domain.Organizations;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Enma.Infrastructure.Persistence.Queries;

namespace Enma.IntegrationTests.Infrastructure.Persistence.Queries;

[Collection(PostgreSqlCollection.Name)]
public sealed class AuditLogReadQueriesTests(PostgreSqlFixture fixture)
    : IAsyncLifetime
{
    private static readonly DateTimeOffset OccurredAt = DateTimeOffset.Parse(
        "2026-08-29T12:00:00Z");

    public Task InitializeAsync() => fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ListAsync_AlwaysTenantQualifiesListAndFilters()
    {
        TestActor current = CreateActor("Current", OrganizationRole.Owner);
        TestActor foreign = CreateActor("Foreign", OrganizationRole.Owner);
        Guid sharedEntityId = Guid.Parse(
            "85ef0a33-083f-43cd-bd68-e460398d4d7a");
        Guid foreignOnlyEntityId = Guid.Parse(
            "e43ea64a-7d63-4afe-aa14-d6f63252f501");
        AuditLog currentLog = CreateAuditLog(
            current,
            Guid.Parse("10000000-0000-0000-0000-000000000001"),
            AuditEventType.ClientCreated,
            sharedEntityId);
        AuditLog foreignSharedLog = CreateAuditLog(
            foreign,
            Guid.Parse("10000000-0000-0000-0000-000000000002"),
            AuditEventType.ClientCreated,
            sharedEntityId);
        AuditLog foreignOnlyLog = CreateAuditLog(
            foreign,
            Guid.Parse("10000000-0000-0000-0000-000000000003"),
            AuditEventType.ClientCreated,
            foreignOnlyEntityId);
        await SeedAsync(
            [current, foreign],
            [currentLog, foreignSharedLog, foreignOnlyLog]);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        var queries = new AuditLogReadQueries(dbContext);

        AuditLogReadPage list = await queries.ListAsync(CreateQuery(
            current.Organization.Id));
        AuditLogReadPage eventFilter = await queries.ListAsync(CreateQuery(
            current.Organization.Id,
            eventType: AuditEventType.ClientCreated));
        AuditLogReadPage sharedEntityFilter = await queries.ListAsync(CreateQuery(
            current.Organization.Id,
            entityType: AuditEntityType.Client,
            entityId: sharedEntityId));
        AuditLogReadPage foreignEntityFilter = await queries.ListAsync(CreateQuery(
            current.Organization.Id,
            entityType: AuditEntityType.Client,
            entityId: foreignOnlyEntityId));

        Assert.Equal(currentLog.Id, Assert.Single(list.Items).Id);
        Assert.Equal(currentLog.Id, Assert.Single(eventFilter.Items).Id);
        Assert.Equal(currentLog.Id, Assert.Single(sharedEntityFilter.Items).Id);
        Assert.Empty(foreignEntityFilter.Items);
        Assert.Equal(0, foreignEntityFilter.TotalCount);
        Assert.Empty(dbContext.ChangeTracker.Entries());
    }

    [Fact]
    public async Task ListAsync_PaginatesByOccurredAtThenIdWithoutLossOrDuplication()
    {
        TestActor actor = CreateActor("Pagination", OrganizationRole.Administrator);
        AuditLog[] logs = Enumerable.Range(1, 5)
            .Select(value => CreateAuditLog(
                actor,
                Guid.Parse($"20000000-0000-0000-0000-{value:D12}"),
                AuditEventType.ClientCreated,
                Guid.NewGuid()))
            .ToArray();
        await SeedAsync([actor], logs);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        var queries = new AuditLogReadQueries(dbContext);

        AuditLogReadPage first = await queries.ListAsync(CreateQuery(
            actor.Organization.Id,
            pageNumber: 1,
            pageSize: 2));
        AuditLogReadPage second = await queries.ListAsync(CreateQuery(
            actor.Organization.Id,
            pageNumber: 2,
            pageSize: 2));
        AuditLogReadPage third = await queries.ListAsync(CreateQuery(
            actor.Organization.Id,
            pageNumber: 3,
            pageSize: 2));
        AuditLogReadPage empty = await queries.ListAsync(CreateQuery(
            actor.Organization.Id,
            pageNumber: 4,
            pageSize: 2));

        Guid[] expected = logs
            .OrderByDescending(auditLog => auditLog.OccurredAt)
            .ThenByDescending(auditLog => auditLog.Id)
            .Select(auditLog => auditLog.Id)
            .ToArray();
        Guid[] actual = first.Items
            .Concat(second.Items)
            .Concat(third.Items)
            .Select(item => item.Id)
            .ToArray();
        Assert.Equal(expected, actual);
        Assert.Equal(actual.Length, actual.Distinct().Count());
        Assert.All(
            new[] { first, second, third, empty },
            page => Assert.Equal(5, page.TotalCount));
        Assert.Empty(empty.Items);
        Assert.Empty(dbContext.ChangeTracker.Entries());
    }

    [Fact]
    public async Task ListAsync_DeserializesOnlyClosedNullAndTypedDetails()
    {
        TestActor actor = CreateActor("Details", OrganizationRole.Owner);
        Guid oldAssignee = Guid.Parse(
            "01dde222-1dd0-42ec-a197-f84679d3ec9f");
        Guid newAssignee = Guid.Parse(
            "8f9a4e71-cc2a-4f09-be7e-d27610a36d0e");
        AuditLog[] logs =
        [
            CreateAuditLog(
                actor,
                Guid.Parse("30000000-0000-0000-0000-000000000001"),
                AuditEventType.ClientCreated,
                Guid.NewGuid()),
            CreateAuditLog(
                actor,
                Guid.Parse("30000000-0000-0000-0000-000000000002"),
                AuditEventType.OrganizationRenamed,
                actor.Organization.Id,
                new OrganizationRenamedAuditDetails("Old Legal", "New Legal")),
            CreateAuditLog(
                actor,
                Guid.Parse("30000000-0000-0000-0000-000000000003"),
                AuditEventType.OrganizationMembershipRoleChanged,
                Guid.NewGuid(),
                new OrganizationMembershipRoleChangedAuditDetails(
                    OrganizationRole.Member,
                    OrganizationRole.Administrator)),
            CreateAuditLog(
                actor,
                Guid.Parse("30000000-0000-0000-0000-000000000004"),
                AuditEventType.LegalDeadlineDetailsChanged,
                Guid.NewGuid(),
                new LegalDeadlineDetailsChangedAuditDetails(
                    [LegalDeadlineChangedField.Title])),
            CreateAuditLog(
                actor,
                Guid.Parse("30000000-0000-0000-0000-000000000005"),
                AuditEventType.LegalTaskDetailsChanged,
                Guid.NewGuid(),
                new LegalTaskDetailsChangedAuditDetails(
                    [LegalTaskChangedField.Description])),
            CreateAuditLog(
                actor,
                Guid.Parse("30000000-0000-0000-0000-000000000006"),
                AuditEventType.LegalTaskAssigneeChanged,
                Guid.NewGuid(),
                new LegalTaskAssigneeChangedAuditDetails(
                    oldAssignee,
                    newAssignee)),
            CreateAuditLog(
                actor,
                Guid.Parse("30000000-0000-0000-0000-000000000007"),
                AuditEventType.CalendarEventUpdated,
                Guid.NewGuid(),
                new CalendarEventUpdatedAuditDetails(
                    [CalendarEventChangedField.Location])),
            CreateAuditLog(
                actor,
                Guid.Parse("30000000-0000-0000-0000-000000000008"),
                AuditEventType.CalendarEventAssigneeChanged,
                Guid.NewGuid(),
                new CalendarEventAssigneeChangedAuditDetails(
                    null,
                    newAssignee))
        ];
        await SeedAsync([actor], logs);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        var queries = new AuditLogReadQueries(dbContext);

        AuditLogReadPage page = await queries.ListAsync(CreateQuery(
            actor.Organization.Id,
            pageSize: 20));

        Assert.Collection(
            page.Items.OrderBy(item => item.Id),
            item => Assert.Null(item.Details),
            item => Assert.IsType<OrganizationRenamedAuditDetails>(item.Details),
            item => Assert.IsType<OrganizationMembershipRoleChangedAuditDetails>(
                item.Details),
            item => Assert.IsType<LegalDeadlineDetailsChangedAuditDetails>(
                item.Details),
            item => Assert.IsType<LegalTaskDetailsChangedAuditDetails>(item.Details),
            item => Assert.IsType<LegalTaskAssigneeChangedAuditDetails>(item.Details),
            item => Assert.IsType<CalendarEventUpdatedAuditDetails>(item.Details),
            item => Assert.IsType<CalendarEventAssigneeChangedAuditDetails>(
                item.Details));
        Assert.Empty(dbContext.ChangeTracker.Entries());
    }

    private static AuditLogReadQuery CreateQuery(
        Guid organizationId,
        AuditEventType? eventType = null,
        AuditEntityType? entityType = null,
        Guid? entityId = null,
        int pageNumber = 1,
        int pageSize = 20)
    {
        return new AuditLogReadQuery(
            organizationId,
            eventType,
            entityType,
            entityId,
            pageNumber,
            pageSize);
    }

    private async Task SeedAsync(
        IReadOnlyCollection<TestActor> actors,
        IReadOnlyCollection<AuditLog> auditLogs)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.Organizations.AddRange(actors.Select(actor => actor.Organization));
        dbContext.Users.AddRange(actors.Select(actor => actor.User));
        dbContext.OrganizationMemberships.AddRange(
            actors.Select(actor => actor.Membership));
        dbContext.AuditLogs.AddRange(auditLogs);
        await dbContext.SaveChangesAsync();
    }

    private static TestActor CreateActor(string marker, OrganizationRole role)
    {
        var organization = new Organization(
            $"{marker} Legal",
            $"{marker.ToLowerInvariant()}-{Guid.NewGuid():N}",
            OccurredAt.AddHours(-2));
        var user = new User(
            $"{marker} Actor",
            $"{marker.ToLowerInvariant()}-{Guid.NewGuid():N}@example.test",
            OccurredAt.AddHours(-2));
        var membership = new OrganizationMembership(
            organization.Id,
            user.Id,
            role,
            OccurredAt.AddHours(-1));
        return new TestActor(organization, user, membership);
    }

    private static AuditLog CreateAuditLog(
        TestActor actor,
        Guid id,
        AuditEventType eventType,
        Guid entityId,
        AuditEventDetails? details = null)
    {
        MethodInfo factory = typeof(AuditLog).GetMethod(
            "CreateAuthoritative",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                "The authoritative audit factory was not found.");

        return (AuditLog)(factory.Invoke(
            null,
            [
                id,
                actor.Organization.Id,
                actor.User.Id,
                actor.Membership.Id,
                actor.Membership.Role,
                eventType,
                eventType.GetEntityType(),
                entityId,
                OccurredAt,
                details,
                null
            ]) ?? throw new InvalidOperationException(
                "The authoritative audit factory returned no audit log."));
    }

    private sealed record TestActor(
        Organization Organization,
        User User,
        OrganizationMembership Membership);
}
