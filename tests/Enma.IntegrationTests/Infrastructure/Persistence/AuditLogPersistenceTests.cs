using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using Enma.Application.Abstractions;
using Enma.Domain.Auditing;
using Enma.Domain.CalendarEvents;
using Enma.Domain.Organizations;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Enma.IntegrationTests.Infrastructure.Persistence;

[Collection(PostgreSqlCollection.Name)]
public sealed class AuditLogPersistenceTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private const string TraceId = "0123456789abcdef0123456789abcdef";
    private static readonly DateTimeOffset OccurredAt = new(
        2026,
        8,
        27,
        15,
        30,
        0,
        TimeSpan.Zero);
    private static readonly MethodInfo AuthoritativeFactory =
        typeof(AuditLog).GetMethod(
            "CreateAuthoritative",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException(
            "AuditLog authoritative factory was not found.");

    public Task InitializeAsync() => fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [MemberData(nameof(DetailsCases))]
    public async Task SaveChangesAsync_WithClosedDetails_RoundTripsExactContract(
        AuditEventType eventType,
        AuditEntityType entityType,
        AuditEventDetails details,
        string[] expectedProperties)
    {
        ActorGraph graph = await SeedActorsAsync();
        AuditLog auditLog = CreateAuditLog(
            graph,
            eventType,
            entityType,
            details: details);

        await using (EnmaDbContext writeContext = fixture.CreateDbContext())
        {
            writeContext.AuditLogs.Add(auditLog);
            await writeContext.SaveChangesAsync();
        }

        await using EnmaDbContext readContext = fixture.CreateDbContext();
        AuditLog persisted = await readContext.AuditLogs.SingleAsync();
        string persistedJson = await ReadDetailsJsonAsync(auditLog.Id);

        Assert.Equal(details.GetType(), persisted.Details?.GetType());
        Assert.Equal(
            JsonSerializer.Serialize(
                details,
                details.GetType(),
                JsonSerializerOptions.Web),
            JsonSerializer.Serialize(
                persisted.Details,
                persisted.Details!.GetType(),
                JsonSerializerOptions.Web));

        using JsonDocument document = JsonDocument.Parse(persistedJson);
        string[] actualProperties = document.RootElement
            .EnumerateObject()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            expectedProperties.Order(StringComparer.Ordinal),
            actualProperties);
        Assert.DoesNotContain("$type", persistedJson, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "assembly",
            persistedJson,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SaveChangesAsync_WithNullDetailsAndTrace_RoundTripsNulls()
    {
        ActorGraph graph = await SeedActorsAsync();
        AuditLog auditLog = CreateAuditLog(graph, traceId: null);

        await using (EnmaDbContext writeContext = fixture.CreateDbContext())
        {
            writeContext.AuditLogs.Add(auditLog);
            await writeContext.SaveChangesAsync();
        }

        await using EnmaDbContext readContext = fixture.CreateDbContext();
        AuditLog persisted = await readContext.AuditLogs.SingleAsync();

        Assert.Null(persisted.Details);
        Assert.Null(persisted.TraceId);
    }

    [Theory]
    [InlineData("cross-tenant-membership")]
    [InlineData("different-user")]
    [InlineData("mismatched-organization")]
    [InlineData("missing-membership")]
    public async Task SaveChangesAsync_WithInvalidActorTuple_RejectsRow(
        string scenario)
    {
        ActorGraph graph = await SeedActorsAsync();
        (Guid organizationId, Guid membershipId, Guid userId) = scenario switch
        {
            "cross-tenant-membership" =>
                (graph.OrganizationA.Id, graph.MembershipB.Id, graph.UserB.Id),
            "different-user" =>
                (graph.OrganizationA.Id, graph.MembershipA.Id, graph.UserB.Id),
            "mismatched-organization" =>
                (graph.OrganizationB.Id, graph.MembershipA.Id, graph.UserA.Id),
            "missing-membership" =>
                (graph.OrganizationA.Id, Guid.NewGuid(), graph.UserA.Id),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        AuditLog auditLog = CreateAuditLog(
            graph,
            organizationId: organizationId,
            actorMembershipId: membershipId,
            actorUserId: userId);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.AuditLogs.Add(auditLog);

        DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => dbContext.SaveChangesAsync());
        PostgresException postgresException = Assert.IsType<PostgresException>(
            exception.InnerException);
        Assert.Equal(
            PostgresErrorCodes.ForeignKeyViolation,
            postgresException.SqlState);
        Assert.Equal(
            "fk_audit_logs_memberships_org_membership_user_id",
            postgresException.ConstraintName);
    }

    [Fact]
    public async Task ActorLifecycleChanges_PreserveHistoricalSnapshot()
    {
        ActorGraph graph = await SeedActorsAsync();
        AuditLog auditLog = CreateAuditLog(graph);

        await using (EnmaDbContext auditContext = fixture.CreateDbContext())
        {
            auditContext.AuditLogs.Add(auditLog);
            await auditContext.SaveChangesAsync();
        }

        await using (EnmaDbContext lifecycleContext = fixture.CreateDbContext())
        {
            OrganizationMembership membership = await lifecycleContext
                .OrganizationMemberships
                .SingleAsync(candidate => candidate.Id == graph.MembershipA.Id);
            User user = await lifecycleContext.Users.SingleAsync(
                candidate => candidate.Id == graph.UserA.Id);

            membership.ChangeRole(OrganizationRole.Administrator);
            membership.Deactivate();
            user.Deactivate();
            await lifecycleContext.SaveChangesAsync();
        }

        await using EnmaDbContext readContext = fixture.CreateDbContext();
        AuditLog persisted = await readContext.AuditLogs.SingleAsync();

        Assert.Equal(OrganizationRole.Member, persisted.ActorRoleAtOccurrence);
        Assert.Equal(graph.MembershipA.Id, persisted.ActorMembershipId);
        Assert.Equal(graph.UserA.Id, persisted.ActorUserId);
    }

    [Theory]
    [InlineData(999, 7, 3)]
    [InlineData(23, 999, 3)]
    [InlineData(23, 7, 999)]
    [InlineData(23, 8, 3)]
    public async Task RawInsert_WithInvalidTaxonomyOrRole_RejectsRow(
        int eventType,
        int entityType,
        int actorRole)
    {
        ActorGraph graph = await SeedActorsAsync();

        PostgresException exception = await InsertRawAuditLogExpectingFailureAsync(
            graph,
            eventType,
            entityType,
            actorRole,
            detailsJson: null,
            traceId: TraceId);

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcde")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("00000000000000000000000000000000")]
    public async Task RawInsert_WithMalformedTraceId_RejectsRow(string traceId)
    {
        ActorGraph graph = await SeedActorsAsync();

        PostgresException exception = await InsertRawAuditLogExpectingFailureAsync(
            graph,
            (int)AuditEventType.CalendarEventDeleted,
            (int)AuditEntityType.CalendarEvent,
            (int)OrganizationRole.Member,
            detailsJson: null,
            traceId: traceId);

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal("ck_audit_logs_trace_id", exception.ConstraintName);
    }

    [Fact]
    public async Task SaveChangesAsync_WhenAuditLogIsModifiedOrDeleted_RejectsMutation()
    {
        ActorGraph graph = await SeedActorsAsync();
        AuditLog auditLog = CreateAuditLog(graph);

        await using (EnmaDbContext insertContext = fixture.CreateDbContext())
        {
            insertContext.AuditLogs.Add(auditLog);
            await insertContext.SaveChangesAsync();
        }

        await using (EnmaDbContext updateContext = fixture.CreateDbContext())
        {
            AuditLog persisted = await updateContext.AuditLogs.SingleAsync();
            updateContext.Entry(persisted).State = EntityState.Modified;

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => updateContext.SaveChangesAsync());
        }

        await using (EnmaDbContext deleteContext = fixture.CreateDbContext())
        {
            AuditLog persisted = await deleteContext.AuditLogs.SingleAsync();
            deleteContext.AuditLogs.Remove(persisted);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => ((IUnitOfWork)deleteContext).SaveChangesAsync());
        }

        await using EnmaDbContext readContext = fixture.CreateDbContext();
        Assert.Equal(1, await readContext.AuditLogs.CountAsync());
    }

    [Fact]
    public async Task RawSql_UpdateDeleteAndTruncate_AreRejected()
    {
        ActorGraph graph = await SeedActorsAsync();
        AuditLog auditLog = CreateAuditLog(graph);

        await using (EnmaDbContext dbContext = fixture.CreateDbContext())
        {
            dbContext.AuditLogs.Add(auditLog);
            await dbContext.SaveChangesAsync();
        }

        foreach (string commandText in new[]
        {
            "UPDATE audit_logs SET occurred_at = occurred_at WHERE id = @id",
            "DELETE FROM audit_logs WHERE id = @id",
            "TRUNCATE TABLE audit_logs"
        })
        {
            PostgresException exception = await ExecuteMutationExpectingFailureAsync(
                commandText,
                auditLog.Id);
            Assert.Equal("55000", exception.SqlState);
            Assert.Contains(
                "append-only",
                exception.MessageText,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task FixtureReset_RemovesRowsAndRestoresTrigger()
    {
        ActorGraph graph = await SeedActorsAsync();

        await using (EnmaDbContext writeContext = fixture.CreateDbContext())
        {
            writeContext.AuditLogs.Add(CreateAuditLog(graph));
            await writeContext.SaveChangesAsync();
        }

        await fixture.ResetDatabaseAsync();

        await using EnmaDbContext readContext = fixture.CreateDbContext();
        Assert.Equal(0, await readContext.AuditLogs.CountAsync());

        PostgresException exception = await ExecuteMutationExpectingFailureAsync(
            "UPDATE audit_logs SET occurred_at = occurred_at",
            Guid.NewGuid());
        Assert.Equal("55000", exception.SqlState);
    }

    [Fact]
    public async Task DetailsSize_EnforcesActualPostgreSqlByteLimit()
    {
        ActorGraph graph = await SeedActorsAsync();
        OrganizationRenamedAuditDetails boundaryDetails =
            CreateOrganizationRenamedDetailsWithSerializedSize(8_189);
        AuditLog boundaryAuditLog = CreateAuditLog(
            graph,
            AuditEventType.OrganizationRenamed,
            AuditEntityType.Organization,
            details: boundaryDetails);

        await using (EnmaDbContext validContext = fixture.CreateDbContext())
        {
            validContext.AuditLogs.Add(boundaryAuditLog);
            await validContext.SaveChangesAsync();
        }

        Assert.Equal(8_192, await ReadStoredDetailsSizeAsync(boundaryAuditLog.Id));

        OrganizationRenamedAuditDetails oversizedDetails =
            CreateOrganizationRenamedDetailsWithSerializedSize(8_193);
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateAuditLog(
            graph,
            AuditEventType.OrganizationRenamed,
            AuditEntityType.Organization,
            details: oversizedDetails));

        AuditLog databaseGuardProbe = CreateAuditLog(
            graph,
            AuditEventType.OrganizationRenamed,
            AuditEntityType.Organization,
            details: new OrganizationRenamedAuditDetails("Old", "New"));
        string oversizedJson = JsonSerializer.Serialize(
            oversizedDetails,
            oversizedDetails.GetType(),
            JsonSerializerOptions.Web);

        await using EnmaDbContext invalidContext = fixture.CreateDbContext();
        invalidContext.AuditLogs.Add(databaseGuardProbe);
        invalidContext.Entry(databaseGuardProbe)
            .Property<string>("_detailsJson")
            .CurrentValue = oversizedJson;

        DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => invalidContext.SaveChangesAsync());
        PostgresException postgresException = Assert.IsType<PostgresException>(
            exception.InnerException);
        Assert.Equal(PostgresErrorCodes.CheckViolation, postgresException.SqlState);
        Assert.Equal("ck_audit_logs_details_size", postgresException.ConstraintName);
    }

    [Fact]
    public async Task AuditLog_SurvivesHardDeletionOfPolymorphicSubject()
    {
        ActorGraph graph = await SeedActorsAsync();
        var calendarEvent = new CalendarEvent(
            graph.OrganizationA.Id,
            "Disposable subject",
            null,
            OccurredAt.AddDays(1),
            OccurredAt.AddDays(1).AddHours(1),
            null,
            null,
            null,
            null,
            graph.MembershipA.Id,
            OccurredAt);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.CalendarEvents.Add(calendarEvent);
        await dbContext.SaveChangesAsync();

        dbContext.AuditLogs.Add(CreateAuditLog(
            graph,
            entityId: calendarEvent.Id));
        await dbContext.SaveChangesAsync();

        dbContext.CalendarEvents.Remove(calendarEvent);
        await dbContext.SaveChangesAsync();

        Assert.Equal(0, await dbContext.CalendarEvents.CountAsync());
        Assert.Equal(1, await dbContext.AuditLogs.CountAsync());
    }

    private async Task<ActorGraph> SeedActorsAsync()
    {
        var organizationA = new Organization(
            "Audit Tenant A",
            "audit-tenant-a",
            OccurredAt.AddDays(-1));
        var userA = new User(
            "Audit Actor A",
            "audit.actor.a@example.test",
            OccurredAt.AddDays(-1));
        var membershipA = new OrganizationMembership(
            organizationA.Id,
            userA.Id,
            OrganizationRole.Member,
            OccurredAt.AddDays(-1));
        var organizationB = new Organization(
            "Audit Tenant B",
            "audit-tenant-b",
            OccurredAt.AddDays(-1));
        var userB = new User(
            "Audit Actor B",
            "audit.actor.b@example.test",
            OccurredAt.AddDays(-1));
        var membershipB = new OrganizationMembership(
            organizationB.Id,
            userB.Id,
            OrganizationRole.Administrator,
            OccurredAt.AddDays(-1));

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.AddRange(
            organizationA,
            userA,
            membershipA,
            organizationB,
            userB,
            membershipB);
        await dbContext.SaveChangesAsync();

        return new ActorGraph(
            organizationA,
            userA,
            membershipA,
            organizationB,
            userB,
            membershipB);
    }

    private static AuditLog CreateAuditLog(
        ActorGraph graph,
        AuditEventType eventType = AuditEventType.CalendarEventDeleted,
        AuditEntityType entityType = AuditEntityType.CalendarEvent,
        Guid? organizationId = null,
        Guid? actorUserId = null,
        Guid? actorMembershipId = null,
        Guid? entityId = null,
        AuditEventDetails? details = null,
        string? traceId = TraceId)
    {
        try
        {
            return (AuditLog)AuthoritativeFactory.Invoke(
                null,
                [
                    Guid.NewGuid(),
                    organizationId ?? graph.OrganizationA.Id,
                    actorUserId ?? graph.UserA.Id,
                    actorMembershipId ?? graph.MembershipA.Id,
                    OrganizationRole.Member,
                    eventType,
                    entityType,
                    entityId ?? Guid.NewGuid(),
                    OccurredAt,
                    details,
                    traceId
                ])!;
        }
        catch (TargetInvocationException exception)
            when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    private async Task<PostgresException> InsertRawAuditLogExpectingFailureAsync(
        ActorGraph graph,
        int eventType,
        int entityType,
        int actorRole,
        string? detailsJson,
        string? traceId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO audit_logs
            (
                id,
                organization_id,
                actor_user_id,
                actor_membership_id,
                actor_role_at_occurrence,
                event_type,
                entity_type,
                entity_id,
                occurred_at,
                details,
                trace_id
            )
            VALUES
            (
                @id,
                @organizationId,
                @actorUserId,
                @actorMembershipId,
                @actorRole,
                @eventType,
                @entityType,
                @entityId,
                @occurredAt,
                @details,
                @traceId
            )
            """,
            connection);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue(
            "organizationId",
            graph.OrganizationA.Id);
        command.Parameters.AddWithValue("actorUserId", graph.UserA.Id);
        command.Parameters.AddWithValue(
            "actorMembershipId",
            graph.MembershipA.Id);
        command.Parameters.AddWithValue("actorRole", actorRole);
        command.Parameters.AddWithValue("eventType", eventType);
        command.Parameters.AddWithValue("entityType", entityType);
        command.Parameters.AddWithValue("entityId", Guid.NewGuid());
        command.Parameters.AddWithValue("occurredAt", OccurredAt);
        command.Parameters.Add(
            new NpgsqlParameter("details", NpgsqlDbType.Jsonb)
            {
                Value = detailsJson is null ? DBNull.Value : detailsJson
            });
        command.Parameters.Add(
            new NpgsqlParameter("traceId", NpgsqlDbType.Varchar)
            {
                Value = traceId is null ? DBNull.Value : traceId
            });

        return await Assert.ThrowsAsync<PostgresException>(
            () => command.ExecuteNonQueryAsync());
    }

    private async Task<PostgresException> ExecuteMutationExpectingFailureAsync(
        string commandText,
        Guid auditLogId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(commandText, connection);
        command.Parameters.AddWithValue("id", auditLogId);

        return await Assert.ThrowsAsync<PostgresException>(
            () => command.ExecuteNonQueryAsync());
    }

    private async Task<string> ReadDetailsJsonAsync(Guid auditLogId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT details::text FROM audit_logs WHERE id = @id",
            connection);
        command.Parameters.AddWithValue("id", auditLogId);

        return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }

    private async Task<int> ReadStoredDetailsSizeAsync(Guid auditLogId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT octet_length(convert_to(details::text, 'UTF8'))
            FROM audit_logs
            WHERE id = @id
            """,
            connection);
        command.Parameters.AddWithValue("id", auditLogId);

        return Assert.IsType<int>(await command.ExecuteScalarAsync());
    }

    private static OrganizationRenamedAuditDetails
        CreateOrganizationRenamedDetailsWithSerializedSize(int sizeInBytes)
    {
        var probe = new OrganizationRenamedAuditDetails("x", "New");
        int fixedSize = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(
                probe,
                probe.GetType(),
                JsonSerializerOptions.Web)) - 1;

        return new OrganizationRenamedAuditDetails(
            new string('a', sizeInBytes - fixedSize),
            "New");
    }

    public static TheoryData<
        AuditEventType,
        AuditEntityType,
        AuditEventDetails,
        string[]> DetailsCases =>
        new()
        {
            {
                AuditEventType.OrganizationRenamed,
                AuditEntityType.Organization,
                new OrganizationRenamedAuditDetails("Old", "New"),
                ["oldName", "newName"]
            },
            {
                AuditEventType.OrganizationMembershipRoleChanged,
                AuditEntityType.OrganizationMembership,
                new OrganizationMembershipRoleChangedAuditDetails(
                    OrganizationRole.Member,
                    OrganizationRole.Administrator),
                ["oldRole", "newRole"]
            },
            {
                AuditEventType.OrganizationInvitationCreated,
                AuditEntityType.OrganizationInvitation,
                new OrganizationInvitationCreatedAuditDetails(
                    OrganizationRole.Member),
                ["role"]
            },
            {
                AuditEventType.LegalDeadlineDetailsChanged,
                AuditEntityType.LegalDeadline,
                new LegalDeadlineDetailsChangedAuditDetails(
                    [LegalDeadlineChangedField.Title]),
                ["changedFields"]
            },
            {
                AuditEventType.LegalTaskDetailsChanged,
                AuditEntityType.LegalTask,
                new LegalTaskDetailsChangedAuditDetails(
                    [LegalTaskChangedField.ProcessId]),
                ["changedFields"]
            },
            {
                AuditEventType.LegalTaskAssigneeChanged,
                AuditEntityType.LegalTask,
                new LegalTaskAssigneeChangedAuditDetails(
                    Guid.Parse("11111111-1111-1111-1111-111111111111"),
                    Guid.Parse("22222222-2222-2222-2222-222222222222")),
                ["oldAssigneeMembershipId", "newAssigneeMembershipId"]
            },
            {
                AuditEventType.CalendarEventUpdated,
                AuditEntityType.CalendarEvent,
                new CalendarEventUpdatedAuditDetails(
                    [CalendarEventChangedField.StartsAt]),
                ["changedFields"]
            },
            {
                AuditEventType.CalendarEventAssigneeChanged,
                AuditEntityType.CalendarEvent,
                new CalendarEventAssigneeChangedAuditDetails(
                    Guid.Parse("11111111-1111-1111-1111-111111111111"),
                    null),
                ["oldAssigneeMembershipId", "newAssigneeMembershipId"]
            }
        };

    private sealed record ActorGraph(
        Organization OrganizationA,
        User UserA,
        OrganizationMembership MembershipA,
        Organization OrganizationB,
        User UserB,
        OrganizationMembership MembershipB);
}
