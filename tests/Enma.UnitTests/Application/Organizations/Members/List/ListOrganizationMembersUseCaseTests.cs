using Enma.Application.Authorization;
using Enma.Application.Organizations.Members.List;
using Enma.Application.Validation;
using Enma.Domain.Organizations;

namespace Enma.UnitTests.Application.Organizations.Members.List;

public sealed class ListOrganizationMembersUseCaseTests
{
    private static readonly Guid UserId = Guid.Parse(
        "1e831e4d-b8a5-4f38-aec1-70954be6054c");
    private static readonly Guid OrganizationId = Guid.Parse(
        "d2594220-4908-4d77-8bf8-c05a8a606660");
    private static readonly Guid MembershipId = Guid.Parse(
        "8813fd59-f949-4e74-910f-1857a7649438");

    [Fact]
    public async Task ExecuteAsync_MemberActiveRequest_UsesBasicEffectiveActiveView()
    {
        var queries = new RecordingQueries(CreatePage());
        ListOrganizationMembersUseCase useCase = CreateUseCase(
            OrganizationRole.Member,
            queries);
        using var cancellationSource = new CancellationTokenSource();

        ListOrganizationMembersResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            status: null,
            search: "  Alice  ",
            pageNumber: 2,
            pageSize: 10,
            cancellationSource.Token);

        Assert.Equal(ListOrganizationMembersResultStatus.Succeeded, result.Status);
        Assert.NotNull(queries.Query);
        Assert.Equal(OrganizationId, queries.Query.OrganizationId);
        Assert.Equal(
            OrganizationMembershipStatus.Active,
            queries.Query.MembershipStatus);
        Assert.Equal(OrganizationMemberDetailLevel.Basic, queries.Query.DetailLevel);
        Assert.Equal("Alice", queries.Query.Search);
        Assert.Equal(2, queries.Query.PageNumber);
        Assert.Equal(10, queries.Query.PageSize);
        Assert.Equal(cancellationSource.Token, queries.CancellationToken);
        Assert.Equal(3, result.TotalCount);
    }

    [Fact]
    public async Task ExecuteAsync_MemberInactiveRequest_ReturnsDeniedWithoutDataRead()
    {
        var queries = new RecordingQueries(CreatePage());
        ListOrganizationMembersUseCase useCase = CreateUseCase(
            OrganizationRole.Member,
            queries);

        ListOrganizationMembersResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            status: "inactive");

        Assert.Equal(
            ListOrganizationMembersResultStatus.AccessDenied,
            result.Status);
        Assert.Equal(0, queries.CallCount);
    }

    [Theory]
    [InlineData(OrganizationRole.Administrator, "active",
        OrganizationMembershipStatus.Active)]
    [InlineData(OrganizationRole.Administrator, "inactive",
        OrganizationMembershipStatus.Inactive)]
    [InlineData(OrganizationRole.Owner, "active",
        OrganizationMembershipStatus.Active)]
    [InlineData(OrganizationRole.Owner, "inactive",
        OrganizationMembershipStatus.Inactive)]
    public async Task ExecuteAsync_PrivilegedRole_UsesAdministrativeRequestedStatus(
        OrganizationRole role,
        string status,
        OrganizationMembershipStatus expectedStatus)
    {
        var queries = new RecordingQueries(CreatePage());
        ListOrganizationMembersUseCase useCase = CreateUseCase(role, queries);

        ListOrganizationMembersResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            status);

        Assert.Equal(ListOrganizationMembersResultStatus.Succeeded, result.Status);
        Assert.NotNull(queries.Query);
        Assert.Equal(expectedStatus, queries.Query.MembershipStatus);
        Assert.Equal(
            OrganizationMemberDetailLevel.Administrative,
            queries.Query.DetailLevel);
    }

    [Fact]
    public async Task ExecuteAsync_DeniedLiveAccess_PerformsNoDataRead()
    {
        var queries = new RecordingQueries(CreatePage());
        ListOrganizationMembersUseCase useCase = CreateUseCase(
            (OrganizationRole?)null,
            queries);

        ListOrganizationMembersResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId);

        Assert.Equal(
            ListOrganizationMembersResultStatus.AccessDenied,
            result.Status);
        Assert.Equal(0, queries.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_RoleChangedLive_UsesCurrentAuthorizationOnEveryRequest()
    {
        var accessLookup = new MutableAccessLookup(OrganizationRole.Administrator);
        var queries = new RecordingQueries(CreatePage());
        ListOrganizationMembersUseCase useCase = CreateUseCase(
            accessLookup,
            queries);

        ListOrganizationMembersResult administratorResult =
            await useCase.ExecuteAsync(
                UserId,
                OrganizationId,
                status: "inactive");
        accessLookup.Role = OrganizationRole.Member;
        ListOrganizationMembersResult memberResult = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            status: "inactive");

        Assert.Equal(
            ListOrganizationMembersResultStatus.Succeeded,
            administratorResult.Status);
        Assert.Equal(
            ListOrganizationMembersResultStatus.AccessDenied,
            memberResult.Status);
        Assert.Equal(2, accessLookup.CallCount);
        Assert.Equal(1, queries.CallCount);
    }

    [Theory]
    [InlineData("pending", 1, 20)]
    [InlineData("Active", 1, 20)]
    [InlineData("active", 0, 20)]
    [InlineData("active", 1, 0)]
    [InlineData("active", 1, 101)]
    [InlineData("active", int.MaxValue, 100)]
    public async Task ExecuteAsync_InvalidRequest_ThrowsBeforeAuthorizationOrDataRead(
        string status,
        int pageNumber,
        int pageSize)
    {
        var accessLookup = new MutableAccessLookup(OrganizationRole.Owner);
        var queries = new RecordingQueries(CreatePage());
        ListOrganizationMembersUseCase useCase = CreateUseCase(
            accessLookup,
            queries);

        await Assert.ThrowsAsync<RequestValidationException>(() =>
            useCase.ExecuteAsync(
                UserId,
                OrganizationId,
                status,
                pageNumber: pageNumber,
                pageSize: pageSize));

        Assert.Equal(0, accessLookup.CallCount);
        Assert.Equal(0, queries.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_ExcessiveSearch_ThrowsBeforeAuthorizationOrDataRead()
    {
        var accessLookup = new MutableAccessLookup(OrganizationRole.Owner);
        var queries = new RecordingQueries(CreatePage());
        ListOrganizationMembersUseCase useCase = CreateUseCase(
            accessLookup,
            queries);

        await Assert.ThrowsAsync<RequestValidationException>(() =>
            useCase.ExecuteAsync(
                UserId,
                OrganizationId,
                search: new string('x', 151)));

        Assert.Equal(0, accessLookup.CallCount);
        Assert.Equal(0, queries.CallCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ExecuteAsync_BlankSearch_ForwardsNoSearch(string? search)
    {
        var queries = new RecordingQueries(CreatePage());
        ListOrganizationMembersUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            queries);

        await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            search: search);

        Assert.Null(queries.Query?.Search);
    }

    private static ListOrganizationMembersUseCase CreateUseCase(
        OrganizationRole? role,
        RecordingQueries queries)
    {
        return CreateUseCase(new MutableAccessLookup(role), queries);
    }

    private static ListOrganizationMembersUseCase CreateUseCase(
        MutableAccessLookup accessLookup,
        RecordingQueries queries)
    {
        var accessAuthorization = new OrganizationAccessAuthorization(accessLookup);
        var administrationAuthorization =
            new OrganizationAdministrationAuthorization(accessAuthorization);
        return new ListOrganizationMembersUseCase(
            administrationAuthorization,
            queries);
    }

    private static OrganizationMemberAdministrationPage CreatePage()
    {
        return new OrganizationMemberAdministrationPage(
            [
                new OrganizationMemberAdministrationReadModel(
                    Guid.Parse("73452c5e-6bdb-4541-94ae-4907ea8ab691"),
                    "Alice",
                    null,
                    OrganizationRole.Member,
                    null,
                    null)
            ],
            3);
    }

    private sealed class MutableAccessLookup(OrganizationRole? role)
        : IOrganizationAccessLookup
    {
        public OrganizationRole? Role { get; set; } = role;

        public int CallCount { get; private set; }

        public Task<OrganizationRole?> FindActiveRoleAsync(
            Guid userId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Role);
        }

        public Task<OrganizationAccessLookupResult?> FindActiveAccessAsync(
            Guid userId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            OrganizationAccessLookupResult? result = Role.HasValue
                ? new OrganizationAccessLookupResult(
                    userId,
                    organizationId,
                    MembershipId,
                    Role.Value)
                : null;
            return Task.FromResult(result);
        }
    }

    private sealed class RecordingQueries(
        OrganizationMemberAdministrationPage page)
        : IOrganizationMemberAdministrationQueries
    {
        public int CallCount { get; private set; }

        public OrganizationMemberAdministrationQuery? Query { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public Task<OrganizationMemberAdministrationPage> ListAsync(
            OrganizationMemberAdministrationQuery query,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Query = query;
            CancellationToken = cancellationToken;
            return Task.FromResult(page);
        }
    }
}
