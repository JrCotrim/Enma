using Enma.Application.Auditing.List;
using Enma.Application.Authorization;
using Enma.Application.Validation;
using Enma.Domain.Auditing;
using Enma.Domain.Organizations;

namespace Enma.UnitTests.Application.Auditing.List;

public sealed class ListAuditLogsUseCaseTests
{
    private static readonly Guid UserId = Guid.Parse(
        "11b5dc11-e5c7-4fd5-89bf-fb3e0364d543");
    private static readonly Guid OrganizationId = Guid.Parse(
        "2f34da08-2f3f-4cf7-b1c0-baa5bcdf4eca");
    private static readonly Guid MembershipId = Guid.Parse(
        "13097da9-046e-4433-a99c-1b48cb21289b");
    private static readonly Guid EntityId = Guid.Parse(
        "857b9159-a9fc-4371-913f-92c21e56c39a");

    [Theory]
    [InlineData(OrganizationRole.Owner)]
    [InlineData(OrganizationRole.Administrator)]
    public async Task ExecuteAsync_PrivilegedRole_UsesAuthorizedTenantAndFilters(
        OrganizationRole role)
    {
        var queries = new RecordingQueries();
        ListAuditLogsUseCase useCase = CreateUseCase(role, queries);
        using var cancellationSource = new CancellationTokenSource();

        ListAuditLogsResult result = await useCase.ExecuteAsync(
            new ListAuditLogsQuery(
                UserId,
                OrganizationId,
                " legal_task.details_changed ",
                " legal_task ",
                EntityId,
                2,
                10),
            cancellationSource.Token);

        Assert.Equal(ListAuditLogsResultStatus.Succeeded, result.Status);
        Assert.NotNull(queries.Query);
        Assert.Equal(OrganizationId, queries.Query.OrganizationId);
        Assert.Equal(
            AuditEventType.LegalTaskDetailsChanged,
            queries.Query.EventType);
        Assert.Equal(AuditEntityType.LegalTask, queries.Query.EntityType);
        Assert.Equal(EntityId, queries.Query.EntityId);
        Assert.Equal(2, queries.Query.PageNumber);
        Assert.Equal(10, queries.Query.PageSize);
        Assert.Equal(cancellationSource.Token, queries.CancellationToken);
    }

    [Theory]
    [InlineData(OrganizationRole.Member)]
    [InlineData(null)]
    public async Task ExecuteAsync_UnprivilegedOrInactiveAccess_DeniesWithoutRead(
        OrganizationRole? role)
    {
        var queries = new RecordingQueries();
        ListAuditLogsUseCase useCase = CreateUseCase(role, queries);

        ListAuditLogsResult result = await useCase.ExecuteAsync(
            new ListAuditLogsQuery(UserId, OrganizationId));

        Assert.Equal(ListAuditLogsResultStatus.AccessDenied, result.Status);
        Assert.Equal(0, queries.CallCount);
    }

    [Theory]
    [InlineData("Client.created", null, null, 1, 20)]
    [InlineData("unknown", null, null, 1, 20)]
    [InlineData(null, "client", null, 1, 20)]
    [InlineData(null, null, "857b9159-a9fc-4371-913f-92c21e56c39a", 1, 20)]
    [InlineData(null, "unknown", "857b9159-a9fc-4371-913f-92c21e56c39a", 1, 20)]
    [InlineData(null, "client", "00000000-0000-0000-0000-000000000000", 1, 20)]
    [InlineData(null, null, null, 0, 20)]
    [InlineData(null, null, null, 1, 0)]
    [InlineData(null, null, null, 1, 101)]
    [InlineData(null, null, null, int.MaxValue, 100)]
    public async Task ExecuteAsync_InvalidInput_ThrowsBeforeAuthorizationOrRead(
        string? eventType,
        string? entityType,
        string? entityId,
        int pageNumber,
        int pageSize)
    {
        var lookup = new RecordingAccessLookup(OrganizationRole.Owner);
        var queries = new RecordingQueries();
        ListAuditLogsUseCase useCase = CreateUseCase(lookup, queries);

        await Assert.ThrowsAsync<RequestValidationException>(() =>
            useCase.ExecuteAsync(new ListAuditLogsQuery(
                UserId,
                OrganizationId,
                eventType,
                entityType,
                entityId is null ? null : Guid.Parse(entityId),
                pageNumber,
                pageSize)));

        Assert.Equal(0, lookup.CallCount);
        Assert.Equal(0, queries.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_BlankFilters_AreOmitted()
    {
        var queries = new RecordingQueries();
        ListAuditLogsUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            queries);

        await useCase.ExecuteAsync(new ListAuditLogsQuery(
            UserId,
            OrganizationId,
            EventType: "  ",
            EntityType: ""));

        Assert.NotNull(queries.Query);
        Assert.Null(queries.Query.EventType);
        Assert.Null(queries.Query.EntityType);
        Assert.Null(queries.Query.EntityId);
    }

    private static ListAuditLogsUseCase CreateUseCase(
        OrganizationRole? role,
        RecordingQueries queries)
    {
        return CreateUseCase(new RecordingAccessLookup(role), queries);
    }

    private static ListAuditLogsUseCase CreateUseCase(
        RecordingAccessLookup lookup,
        RecordingQueries queries)
    {
        return new ListAuditLogsUseCase(
            new OrganizationAdministrationAuthorization(
                new OrganizationAccessAuthorization(lookup)),
            queries);
    }

    private sealed class RecordingAccessLookup(OrganizationRole? role)
        : IOrganizationAccessLookup
    {
        public int CallCount { get; private set; }

        public Task<OrganizationRole?> FindActiveRoleAsync(
            Guid userId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(role);
        }

        public Task<OrganizationAccessLookupResult?> FindActiveAccessAsync(
            Guid userId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            OrganizationAccessLookupResult? result = role.HasValue
                ? new OrganizationAccessLookupResult(
                    userId,
                    organizationId,
                    MembershipId,
                    role.Value)
                : null;
            return Task.FromResult(result);
        }
    }

    private sealed class RecordingQueries : IAuditLogReadQueries
    {
        public int CallCount { get; private set; }

        public AuditLogReadQuery? Query { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public Task<AuditLogReadPage> ListAsync(
            AuditLogReadQuery query,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Query = query;
            CancellationToken = cancellationToken;
            return Task.FromResult(new AuditLogReadPage([], 0));
        }
    }
}
