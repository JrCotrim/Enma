using Enma.Domain.CalendarEvents;
using Enma.Domain.Clients;
using Enma.Domain.Organizations;
using Enma.Domain.Processes;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Npgsql;

namespace Enma.IntegrationTests.Infrastructure.Persistence;

[Collection(PostgreSqlCollection.Name)]
public sealed class CalendarEventPersistenceTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private const string AssigneeForeignKey =
        "fk_calendar_events_memberships_org_assignee_membership_id";
    private const string ClientForeignKey =
        "fk_calendar_events_clients_organization_id_client_id";
    private const string CreatorForeignKey =
        "fk_calendar_events_memberships_org_created_by_membership_id";
    private const string OrganizationForeignKey =
        "fk_calendar_events_organizations_organization_id";
    private const string ProcessForeignKey =
        "fk_calendar_events_processes_organization_id_process_id";

    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        8,
        22,
        12,
        0,
        0,
        TimeSpan.Zero);

    private static readonly DateTimeOffset StartsAt = CreatedAt.AddDays(2);

    private static readonly DateTimeOffset EndsAt = StartsAt.AddHours(1);

    public Task InitializeAsync()
    {
        return fixture.ResetDatabaseAsync();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    [Fact]
    public async Task SaveChangesAsync_WithAllAssociationKinds_RoundTripsEvents()
    {
        TenantGraph graph = CreateTenantGraph("Alpha", "alpha");
        CalendarEvent generalEvent = CreateCalendarEvent(
            graph,
            "  General Event  ",
            "  General notes  ",
            "  Main Office  ");
        CalendarEvent clientEvent = CreateCalendarEvent(
            graph,
            "Client Event",
            clientId: graph.Client.Id,
            startsAt: StartsAt.AddHours(2),
            endsAt: EndsAt.AddHours(2));
        CalendarEvent processEvent = CreateCalendarEvent(
            graph,
            "Process Event",
            processId: graph.LegalProcess.Id,
            assigneeMembershipId: graph.AssigneeMembership.Id,
            startsAt: StartsAt.AddHours(4),
            endsAt: EndsAt.AddHours(4));

        await SeedAsync(
            GetGraphEntities(graph)
                .Concat([generalEvent, clientEvent, processEvent])
                .ToArray());

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        CalendarEvent[] persistedEvents = await dbContext.CalendarEvents
            .AsNoTracking()
            .OrderBy(calendarEvent => calendarEvent.StartsAt)
            .ToArrayAsync();

        Assert.Equal(3, persistedEvents.Length);

        CalendarEvent persistedGeneralEvent = persistedEvents[0];
        Assert.Equal(generalEvent.Id, persistedGeneralEvent.Id);
        Assert.Equal(graph.Organization.Id, persistedGeneralEvent.OrganizationId);
        Assert.Equal("General Event", persistedGeneralEvent.Title);
        Assert.Equal("General notes", persistedGeneralEvent.Description);
        Assert.Equal("Main Office", persistedGeneralEvent.Location);
        Assert.Equal(StartsAt, persistedGeneralEvent.StartsAt);
        Assert.Equal(EndsAt, persistedGeneralEvent.EndsAt);
        Assert.Null(persistedGeneralEvent.ClientId);
        Assert.Null(persistedGeneralEvent.ProcessId);
        Assert.Null(persistedGeneralEvent.AssigneeMembershipId);
        Assert.Equal(
            graph.CreatorMembership.Id,
            persistedGeneralEvent.CreatedByMembershipId);
        Assert.Equal(CreatedAt, persistedGeneralEvent.CreatedAt);

        Assert.Equal(graph.Client.Id, persistedEvents[1].ClientId);
        Assert.Null(persistedEvents[1].ProcessId);
        Assert.Null(persistedEvents[2].ClientId);
        Assert.Equal(graph.LegalProcess.Id, persistedEvents[2].ProcessId);
        Assert.Equal(
            graph.AssigneeMembership.Id,
            persistedEvents[2].AssigneeMembershipId);
    }

    [Fact]
    public async Task SaveChangesAsync_WithMinusThreeOffset_RoundTripsUtcInstants()
    {
        TenantGraph graph = CreateTenantGraph("Alpha", "alpha");
        DateTimeOffset startsAt = new(
            2026,
            8,
            24,
            10,
            0,
            0,
            TimeSpan.FromHours(-3));
        DateTimeOffset endsAt = startsAt.AddHours(1);
        DateTimeOffset createdAt = new(
            2026,
            8,
            22,
            9,
            0,
            0,
            TimeSpan.FromHours(-3));
        var calendarEvent = new CalendarEvent(
            graph.Organization.Id,
            "Offset-safe Event",
            null,
            startsAt,
            endsAt,
            null,
            null,
            null,
            null,
            graph.CreatorMembership.Id,
            createdAt);

        await SeedAsync(
            GetGraphEntities(graph)
                .Append(calendarEvent)
                .ToArray());

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        CalendarEvent persistedEvent = await dbContext.CalendarEvents
            .AsNoTracking()
            .SingleAsync();

        Assert.Equal(TimeSpan.Zero, persistedEvent.StartsAt.Offset);
        Assert.Equal(TimeSpan.Zero, persistedEvent.EndsAt.Offset);
        Assert.Equal(TimeSpan.Zero, persistedEvent.CreatedAt.Offset);
        Assert.Equal(startsAt.UtcDateTime, persistedEvent.StartsAt.UtcDateTime);
        Assert.Equal(endsAt.UtcDateTime, persistedEvent.EndsAt.UtcDateTime);
        Assert.Equal(createdAt.UtcDateTime, persistedEvent.CreatedAt.UtcDateTime);
    }

    [Fact]
    public async Task SaveChangesAsync_WithCrossTenantClient_RejectsEvent()
    {
        TenantGraph graphA = CreateTenantGraph("Alpha", "alpha");
        TenantGraph graphB = CreateTenantGraph("Beta", "beta");
        await SeedAsync(
            GetGraphEntities(graphA)
                .Concat(GetGraphEntities(graphB))
                .ToArray());
        CalendarEvent calendarEvent = CreateCalendarEvent(
            graphA,
            "Cross-tenant Client",
            clientId: graphB.Client.Id);

        await AssertForeignKeyViolationAsync(calendarEvent, ClientForeignKey);
    }

    [Fact]
    public async Task SaveChangesAsync_WithCrossTenantProcess_RejectsEvent()
    {
        TenantGraph graphA = CreateTenantGraph("Alpha", "alpha");
        TenantGraph graphB = CreateTenantGraph("Beta", "beta");
        await SeedAsync(
            GetGraphEntities(graphA)
                .Concat(GetGraphEntities(graphB))
                .ToArray());
        CalendarEvent calendarEvent = CreateCalendarEvent(
            graphA,
            "Cross-tenant Process",
            processId: graphB.LegalProcess.Id);

        await AssertForeignKeyViolationAsync(calendarEvent, ProcessForeignKey);
    }

    [Fact]
    public async Task SaveChangesAsync_WithCrossTenantAssignee_RejectsEvent()
    {
        TenantGraph graphA = CreateTenantGraph("Alpha", "alpha");
        TenantGraph graphB = CreateTenantGraph("Beta", "beta");
        await SeedAsync(
            GetGraphEntities(graphA)
                .Concat(GetGraphEntities(graphB))
                .ToArray());
        CalendarEvent calendarEvent = CreateCalendarEvent(
            graphA,
            "Cross-tenant Assignee",
            assigneeMembershipId: graphB.AssigneeMembership.Id);

        await AssertForeignKeyViolationAsync(calendarEvent, AssigneeForeignKey);
    }

    [Fact]
    public async Task SaveChangesAsync_WithCrossTenantCreator_RejectsEvent()
    {
        TenantGraph graphA = CreateTenantGraph("Alpha", "alpha");
        TenantGraph graphB = CreateTenantGraph("Beta", "beta");
        await SeedAsync(
            GetGraphEntities(graphA)
                .Concat(GetGraphEntities(graphB))
                .ToArray());
        CalendarEvent calendarEvent = new(
            graphA.Organization.Id,
            "Cross-tenant Creator",
            null,
            StartsAt,
            EndsAt,
            null,
            null,
            null,
            null,
            graphB.CreatorMembership.Id,
            CreatedAt);

        await AssertForeignKeyViolationAsync(calendarEvent, CreatorForeignKey);
    }

    [Fact]
    public async Task SaveChangesAsync_WhenDeletingReferencedRows_RestrictsDelete()
    {
        TenantGraph graph = CreateTenantGraph("Alpha", "alpha");
        CalendarEvent calendarEvent = CreateCalendarEvent(
            graph,
            "Restricted Event",
            processId: graph.LegalProcess.Id,
            assigneeMembershipId: graph.AssigneeMembership.Id);
        await SeedAsync(
            GetGraphEntities(graph)
                .Append(calendarEvent)
                .ToArray());
        await using EnmaDbContext dbContext = fixture.CreateDbContext();

        await AssertDeleteRestrictedAsync(
            () => dbContext.LegalProcesses
                .Where(process => process.Id == graph.LegalProcess.Id)
                .ExecuteDeleteAsync(),
            ProcessForeignKey);
        await AssertDeleteRestrictedAsync(
            () => dbContext.OrganizationMemberships
                .Where(membership =>
                    membership.Id == graph.AssigneeMembership.Id)
                .ExecuteDeleteAsync(),
            AssigneeForeignKey);
        await AssertDeleteRestrictedAsync(
            () => dbContext.OrganizationMemberships
                .Where(membership =>
                    membership.Id == graph.CreatorMembership.Id)
                .ExecuteDeleteAsync(),
            CreatorForeignKey);
    }

    [Theory]
    [InlineData("title", "ck_calendar_events_title_normalized")]
    [InlineData("description", "ck_calendar_events_description_normalized")]
    [InlineData("location", "ck_calendar_events_location_normalized")]
    [InlineData("association", "ck_calendar_events_association")]
    [InlineData("equal-time", "ck_calendar_events_time_range")]
    [InlineData("reverse-time", "ck_calendar_events_time_range")]
    public async Task DatabaseInsert_WithInvalidValues_EnforcesCheckConstraint(
        string invalidField,
        string expectedConstraintName)
    {
        TenantGraph graph = CreateTenantGraph("Alpha", "alpha");
        await SeedAsync(GetGraphEntities(graph));

        PostgresException exception = await AssertInvalidDirectInsertAsync(
            graph,
            title: invalidField == "title" ? "  Padded  " : "Valid Event",
            description: invalidField == "description" ? "  Padded  " : null,
            location: invalidField == "location" ? "   " : null,
            startsAt: StartsAt,
            endsAt: invalidField switch
            {
                "equal-time" => StartsAt,
                "reverse-time" => StartsAt.AddTicks(-1),
                _ => EndsAt
            },
            clientId: invalidField == "association" ? graph.Client.Id : null,
            processId: invalidField == "association"
                ? graph.LegalProcess.Id
                : null);

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal(expectedConstraintName, exception.ConstraintName);
    }

    [Fact]
    public void CalendarEventModel_HasExpectedRelationalMetadata()
    {
        using EnmaDbContext dbContext = fixture.CreateDbContext();
        IEntityType entityType = Assert.IsAssignableFrom<IEntityType>(
            dbContext.Model.FindEntityType(typeof(CalendarEvent)));

        Assert.Equal("calendar_events", entityType.GetTableName());
        Assert.Equal(
            [
                nameof(CalendarEvent.Id),
                nameof(CalendarEvent.OrganizationId),
                nameof(CalendarEvent.Title),
                nameof(CalendarEvent.Description),
                nameof(CalendarEvent.StartsAt),
                nameof(CalendarEvent.EndsAt),
                nameof(CalendarEvent.Location),
                nameof(CalendarEvent.ClientId),
                nameof(CalendarEvent.ProcessId),
                nameof(CalendarEvent.AssigneeMembershipId),
                nameof(CalendarEvent.CreatedByMembershipId),
                nameof(CalendarEvent.CreatedAt)
            ],
            entityType.GetProperties()
                .OrderBy(property => PropertyOrder(property.Name))
                .Select(property => property.Name)
                .ToArray());
        Assert.Equal(
            ValueGenerated.Never,
            entityType.FindProperty(nameof(CalendarEvent.Id))!.ValueGenerated);
        Assert.Equal(
            "varchar(150)",
            entityType.FindProperty(nameof(CalendarEvent.Title))!.GetColumnType());
        Assert.Equal(
            "varchar(2000)",
            entityType.FindProperty(nameof(CalendarEvent.Description))!
                .GetColumnType());
        Assert.Equal(
            "varchar(255)",
            entityType.FindProperty(nameof(CalendarEvent.Location))!.GetColumnType());
        Assert.Equal(
            "timestamp with time zone",
            entityType.FindProperty(nameof(CalendarEvent.StartsAt))!.GetColumnType());
        Assert.Equal(
            "timestamp with time zone",
            entityType.FindProperty(nameof(CalendarEvent.EndsAt))!.GetColumnType());
        Assert.Equal(
            "timestamp with time zone",
            entityType.FindProperty(nameof(CalendarEvent.CreatedAt))!.GetColumnType());

        AssertIndex(
            entityType,
            "ix_calendar_events_organization_id_starts_at_id",
            [
                nameof(CalendarEvent.OrganizationId),
                nameof(CalendarEvent.StartsAt),
                nameof(CalendarEvent.Id)
            ],
            null);
        AssertIndex(
            entityType,
            "ix_calendar_events_org_assignee_starts_at_id",
            [
                nameof(CalendarEvent.OrganizationId),
                nameof(CalendarEvent.AssigneeMembershipId),
                nameof(CalendarEvent.StartsAt),
                nameof(CalendarEvent.Id)
            ],
            "assignee_membership_id IS NOT NULL");
        AssertIndex(
            entityType,
            "ix_calendar_events_org_client_starts_at_id",
            [
                nameof(CalendarEvent.OrganizationId),
                nameof(CalendarEvent.ClientId),
                nameof(CalendarEvent.StartsAt),
                nameof(CalendarEvent.Id)
            ],
            "client_id IS NOT NULL");
        AssertIndex(
            entityType,
            "ix_calendar_events_org_process_starts_at_id",
            [
                nameof(CalendarEvent.OrganizationId),
                nameof(CalendarEvent.ProcessId),
                nameof(CalendarEvent.StartsAt),
                nameof(CalendarEvent.Id)
            ],
            "process_id IS NOT NULL");
        AssertIndex(
            entityType,
            "ix_calendar_events_org_created_by_membership_id",
            [
                nameof(CalendarEvent.OrganizationId),
                nameof(CalendarEvent.CreatedByMembershipId)
            ],
            null);
        Assert.Equal(5, entityType.GetIndexes().Count());

        AssertForeignKey(
            entityType,
            OrganizationForeignKey,
            typeof(Organization),
            [nameof(CalendarEvent.OrganizationId)],
            [nameof(Organization.Id)]);
        AssertForeignKey(
            entityType,
            ClientForeignKey,
            typeof(Client),
            [nameof(CalendarEvent.OrganizationId), nameof(CalendarEvent.ClientId)],
            [nameof(Client.OrganizationId), nameof(Client.Id)]);
        AssertForeignKey(
            entityType,
            ProcessForeignKey,
            typeof(LegalProcess),
            [nameof(CalendarEvent.OrganizationId), nameof(CalendarEvent.ProcessId)],
            [nameof(LegalProcess.OrganizationId), nameof(LegalProcess.Id)]);
        AssertForeignKey(
            entityType,
            AssigneeForeignKey,
            typeof(OrganizationMembership),
            [
                nameof(CalendarEvent.OrganizationId),
                nameof(CalendarEvent.AssigneeMembershipId)
            ],
            [
                nameof(OrganizationMembership.OrganizationId),
                nameof(OrganizationMembership.Id)
            ]);
        AssertForeignKey(
            entityType,
            CreatorForeignKey,
            typeof(OrganizationMembership),
            [
                nameof(CalendarEvent.OrganizationId),
                nameof(CalendarEvent.CreatedByMembershipId)
            ],
            [
                nameof(OrganizationMembership.OrganizationId),
                nameof(OrganizationMembership.Id)
            ]);
        Assert.Equal(5, entityType.GetForeignKeys().Count());
    }

    [Fact]
    public async Task PostgreSqlSchema_WithCalendarEvents_HasExpectedShape()
    {
        Assert.Equal(
            "id,organization_id,title,description,starts_at,ends_at,location," +
            "client_id,process_id,assignee_membership_id," +
            "created_by_membership_id,created_at",
            await GetTableColumnsAsync());
        Assert.Equal(
            "timestamp with time zone",
            await GetColumnTypeAsync("starts_at"));
        Assert.Equal(
            "timestamp with time zone",
            await GetColumnTypeAsync("ends_at"));
        Assert.Equal(
            "timestamp with time zone",
            await GetColumnTypeAsync("created_at"));

        Assert.Equal(
            "organization_id,client_id",
            await GetConstraintColumnsAsync(ClientForeignKey));
        Assert.Equal(
            "organization_id,process_id",
            await GetConstraintColumnsAsync(ProcessForeignKey));
        Assert.Equal(
            "organization_id,assignee_membership_id",
            await GetConstraintColumnsAsync(AssigneeForeignKey));
        Assert.Equal(
            "organization_id,created_by_membership_id",
            await GetConstraintColumnsAsync(CreatorForeignKey));
        Assert.Equal(
            "organization_id",
            await GetConstraintColumnsAsync(OrganizationForeignKey));
        Assert.Equal(
            ["RESTRICT", "RESTRICT", "RESTRICT", "RESTRICT", "RESTRICT"],
            await GetDeleteRulesAsync());

        Assert.Equal(
            [
                "ix_calendar_events_org_assignee_starts_at_id",
                "ix_calendar_events_org_client_starts_at_id",
                "ix_calendar_events_org_created_by_membership_id",
                "ix_calendar_events_org_process_starts_at_id",
                "ix_calendar_events_organization_id_starts_at_id",
                "pk_calendar_events"
            ],
            await GetIndexNamesAsync());
        await AssertIndexDefinitionAsync(
            "ix_calendar_events_organization_id_starts_at_id",
            "(organization_id, starts_at, id)",
            null);
        await AssertIndexDefinitionAsync(
            "ix_calendar_events_org_assignee_starts_at_id",
            "(organization_id, assignee_membership_id, starts_at, id)",
            "WHERE (assignee_membership_id IS NOT NULL)");
        await AssertIndexDefinitionAsync(
            "ix_calendar_events_org_client_starts_at_id",
            "(organization_id, client_id, starts_at, id)",
            "WHERE (client_id IS NOT NULL)");
        await AssertIndexDefinitionAsync(
            "ix_calendar_events_org_process_starts_at_id",
            "(organization_id, process_id, starts_at, id)",
            "WHERE (process_id IS NOT NULL)");

        Assert.Equal(
            [
                "ck_calendar_events_association",
                "ck_calendar_events_description_normalized",
                "ck_calendar_events_location_normalized",
                "ck_calendar_events_time_range",
                "ck_calendar_events_title_normalized"
            ],
            await GetCheckConstraintNamesAsync());
    }

    private async Task AssertForeignKeyViolationAsync(
        CalendarEvent calendarEvent,
        string expectedConstraintName)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.CalendarEvents.Add(calendarEvent);

        DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => dbContext.SaveChangesAsync());
        PostgresException postgresException =
            Assert.IsType<PostgresException>(exception.InnerException);

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, postgresException.SqlState);
        Assert.Equal(expectedConstraintName, postgresException.ConstraintName);
    }

    private static async Task AssertDeleteRestrictedAsync(
        Func<Task<int>> delete,
        string expectedConstraintName)
    {
        PostgresException exception =
            await Assert.ThrowsAsync<PostgresException>(delete);

        Assert.Equal(PostgresErrorCodes.RestrictViolation, exception.SqlState);
        Assert.Equal(expectedConstraintName, exception.ConstraintName);
    }

    private async Task<PostgresException> AssertInvalidDirectInsertAsync(
        TenantGraph graph,
        string title,
        string? description,
        string? location,
        DateTimeOffset startsAt,
        DateTimeOffset endsAt,
        Guid? clientId,
        Guid? processId)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();

        return await Assert.ThrowsAsync<PostgresException>(() =>
            dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO calendar_events
                    (id, organization_id, title, description, starts_at, ends_at,
                     location, client_id, process_id, assignee_membership_id,
                     created_by_membership_id, created_at)
                VALUES
                    ({Guid.NewGuid()}, {graph.Organization.Id}, {title},
                     {description}, {startsAt}, {endsAt}, {location}, {clientId},
                     {processId}, NULL, {graph.CreatorMembership.Id}, {CreatedAt})
                """));
    }

    private async Task AssertIndexDefinitionAsync(
        string indexName,
        string expectedColumns,
        string? expectedPredicate)
    {
        string? indexDefinition = await ExecuteScalarStringAsync(
            """
            SELECT indexdef
            FROM pg_indexes
            WHERE schemaname = 'public'
              AND tablename = 'calendar_events'
              AND indexname = @value
            """,
            indexName);

        Assert.NotNull(indexDefinition);
        Assert.Contains(expectedColumns, indexDefinition);

        if (expectedPredicate is not null)
        {
            Assert.Contains(expectedPredicate, indexDefinition);
        }
    }

    private async Task SeedAsync(params object[] entities)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.AddRange(entities);
        await dbContext.SaveChangesAsync();
    }

    private static object[] GetGraphEntities(TenantGraph graph)
    {
        return
        [
            graph.Organization,
            graph.CreatorUser,
            graph.CreatorMembership,
            graph.AssigneeUser,
            graph.AssigneeMembership,
            graph.Client,
            graph.LegalProcess
        ];
    }

    private static CalendarEvent CreateCalendarEvent(
        TenantGraph graph,
        string title,
        string? description = null,
        string? location = null,
        Guid? clientId = null,
        Guid? processId = null,
        Guid? assigneeMembershipId = null,
        DateTimeOffset? startsAt = null,
        DateTimeOffset? endsAt = null)
    {
        return new CalendarEvent(
            graph.Organization.Id,
            title,
            description,
            startsAt ?? StartsAt,
            endsAt ?? EndsAt,
            location,
            clientId,
            processId,
            assigneeMembershipId,
            graph.CreatorMembership.Id,
            CreatedAt);
    }

    private static TenantGraph CreateTenantGraph(string name, string slug)
    {
        var organization = new Organization(name, slug, CreatedAt);
        var creatorUser = new User(
            $"{name} Creator",
            $"{slug}.creator@example.test",
            CreatedAt);
        var creatorMembership = new OrganizationMembership(
            organization.Id,
            creatorUser.Id,
            OrganizationRole.Administrator,
            CreatedAt);
        var assigneeUser = new User(
            $"{name} Assignee",
            $"{slug}.assignee@example.test",
            CreatedAt);
        var assigneeMembership = new OrganizationMembership(
            organization.Id,
            assigneeUser.Id,
            OrganizationRole.Member,
            CreatedAt);
        var client = new Client(
            organization.Id,
            $"{name} Client",
            CreatedAt);
        var legalProcess = new LegalProcess(
            organization.Id,
            client.Id,
            $"{name} Process",
            CreatedAt);

        return new TenantGraph(
            organization,
            creatorUser,
            creatorMembership,
            assigneeUser,
            assigneeMembership,
            client,
            legalProcess);
    }

    private static int PropertyOrder(string propertyName)
    {
        return Array.IndexOf(
            [
                nameof(CalendarEvent.Id),
                nameof(CalendarEvent.OrganizationId),
                nameof(CalendarEvent.Title),
                nameof(CalendarEvent.Description),
                nameof(CalendarEvent.StartsAt),
                nameof(CalendarEvent.EndsAt),
                nameof(CalendarEvent.Location),
                nameof(CalendarEvent.ClientId),
                nameof(CalendarEvent.ProcessId),
                nameof(CalendarEvent.AssigneeMembershipId),
                nameof(CalendarEvent.CreatedByMembershipId),
                nameof(CalendarEvent.CreatedAt)
            ],
            propertyName);
    }

    private static void AssertIndex(
        IEntityType entityType,
        string databaseName,
        string[] properties,
        string? filter)
    {
        IIndex index = Assert.Single(
            entityType.GetIndexes(),
            candidate => candidate.GetDatabaseName() == databaseName);
        Assert.Equal(
            properties,
            index.Properties.Select(property => property.Name).ToArray());
        Assert.Equal(filter, index.GetFilter());
    }

    private static void AssertForeignKey(
        IEntityType entityType,
        string constraintName,
        Type principalType,
        string[] properties,
        string[] principalProperties)
    {
        IForeignKey foreignKey = Assert.Single(
            entityType.GetForeignKeys(),
            candidate => candidate.GetConstraintName() == constraintName);
        Assert.Equal(principalType, foreignKey.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.Restrict, foreignKey.DeleteBehavior);
        Assert.Equal(
            properties,
            foreignKey.Properties.Select(property => property.Name).ToArray());
        Assert.Equal(
            principalProperties,
            foreignKey.PrincipalKey.Properties
                .Select(property => property.Name)
                .ToArray());
    }

    private async Task<string?> GetConstraintColumnsAsync(string constraintName)
    {
        const string Query =
            """
            SELECT string_agg(kcu.column_name, ',' ORDER BY kcu.ordinal_position)
            FROM information_schema.table_constraints AS tc
            INNER JOIN information_schema.key_column_usage AS kcu
                ON kcu.constraint_schema = tc.constraint_schema
                AND kcu.constraint_name = tc.constraint_name
            WHERE tc.constraint_schema = 'public'
              AND tc.table_name = 'calendar_events'
              AND tc.constraint_name = @constraintName
              AND tc.constraint_type = 'FOREIGN KEY'
            """;

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(Query, connection);
        command.Parameters.AddWithValue("constraintName", constraintName);
        object? result = await command.ExecuteScalarAsync();
        return result is DBNull or null ? null : (string)result;
    }

    private Task<string[]> GetDeleteRulesAsync()
    {
        return ExecuteStringArrayAsync(
            """
            SELECT delete_rule
            FROM information_schema.referential_constraints
            WHERE constraint_schema = 'public'
              AND constraint_name LIKE 'fk_calendar_events_%'
            ORDER BY constraint_name
            """);
    }

    private Task<string[]> GetIndexNamesAsync()
    {
        return ExecuteStringArrayAsync(
            """
            SELECT indexname
            FROM pg_indexes
            WHERE schemaname = 'public'
              AND tablename = 'calendar_events'
            ORDER BY indexname
            """);
    }

    private Task<string?> GetColumnTypeAsync(string columnName)
    {
        return ExecuteScalarStringAsync(
            """
            SELECT data_type
            FROM information_schema.columns
            WHERE table_schema = 'public'
              AND table_name = 'calendar_events'
              AND column_name = @value
            """,
            columnName);
    }

    private async Task<string?> GetTableColumnsAsync()
    {
        const string Query =
            """
            SELECT string_agg(column_name, ',' ORDER BY ordinal_position)
            FROM information_schema.columns
            WHERE table_schema = 'public'
              AND table_name = 'calendar_events'
            """;

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(Query, connection);
        object? result = await command.ExecuteScalarAsync();
        return result is DBNull or null ? null : (string)result;
    }

    private Task<string[]> GetCheckConstraintNamesAsync()
    {
        return ExecuteStringArrayAsync(
            """
            SELECT constraint_name
            FROM information_schema.table_constraints
            WHERE constraint_schema = 'public'
              AND table_name = 'calendar_events'
              AND constraint_type = 'CHECK'
              AND constraint_name LIKE 'ck_calendar_events_%'
            ORDER BY constraint_name
            """);
    }

    private async Task<string?> ExecuteScalarStringAsync(
        string query,
        string value)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(query, connection);
        command.Parameters.AddWithValue("value", value);
        object? result = await command.ExecuteScalarAsync();
        return result is DBNull or null ? null : (string)result;
    }

    private async Task<string[]> ExecuteStringArrayAsync(string query)
    {
        var values = new List<string>();
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(query, connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values.ToArray();
    }

    private sealed record TenantGraph(
        Organization Organization,
        User CreatorUser,
        OrganizationMembership CreatorMembership,
        User AssigneeUser,
        OrganizationMembership AssigneeMembership,
        Client Client,
        LegalProcess LegalProcess);
}
