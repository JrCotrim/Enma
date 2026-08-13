using Enma.Application.Authorization;
using Enma.Application.Processes;
using Enma.Application.Processes.List;
using Enma.Application.Validation;
using Enma.Domain.Organizations;

namespace Enma.UnitTests.Application.Processes.List;

public sealed class ListLegalProcessesUseCaseTests
{
    private static readonly Guid UserId = Guid.Parse(
        "5d504351-0ec8-4394-9317-808445fc52b4");

    private static readonly Guid OrganizationId = Guid.Parse(
        "be443985-22db-4e4a-8b49-1b61eb2b5312");

    [Fact]
    public async Task ExecuteAsync_WithDeniedView_DeniesWithoutProcessQuery()
    {
        var queries = new FakeLegalProcessReadQueries();
        ListLegalProcessesUseCase useCase = CreateUseCase(
            (OrganizationRole?)null,
            queries);

        ListLegalProcessesResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId);

        Assert.Equal(ListLegalProcessesResultStatus.AccessDenied, result.Status);
        Assert.Empty(result.Items);
        Assert.Equal(0, queries.ListCallCount);
    }

    [Theory]
    [InlineData(OrganizationRole.Owner)]
    [InlineData(OrganizationRole.Administrator)]
    [InlineData(OrganizationRole.Member)]
    public async Task ExecuteAsync_WithViewRole_QueriesOnlyContextualOrganization(
        OrganizationRole role)
    {
        LegalProcessReadModel[] legalProcesses =
        [
            new(
                Guid.Parse("77a319ea-840c-47bb-935f-944191520287"),
                "Contract Review",
                Guid.Parse("381c55b5-1d4d-4143-8071-521ac89cde66"),
                "Acme Legal",
                DateTimeOffset.Parse("2026-08-13T14:00:00+00:00"))
        ];
        var queries = new FakeLegalProcessReadQueries(legalProcesses);
        ListLegalProcessesUseCase useCase = CreateUseCase(role, queries);

        ListLegalProcessesResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            2,
            10);

        Assert.Equal(ListLegalProcessesResultStatus.Succeeded, result.Status);
        Assert.Equal(legalProcesses, result.Items);
        Assert.Equal(OrganizationId, queries.OrganizationId);
        Assert.Equal(2, queries.PageNumber);
        Assert.Equal(10, queries.PageSize);
        Assert.Equal(2, result.PageNumber);
        Assert.Equal(10, result.PageSize);
    }

    [Fact]
    public async Task ExecuteAsync_WithoutExplicitPagination_UsesBoundedDefaults()
    {
        var queries = new FakeLegalProcessReadQueries();
        ListLegalProcessesUseCase useCase = CreateUseCase(
            OrganizationRole.Member,
            queries);

        ListLegalProcessesResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId);

        Assert.Equal(1, result.PageNumber);
        Assert.Equal(ListLegalProcessesUseCase.DefaultPageSize, result.PageSize);
        Assert.Equal(1, queries.PageNumber);
        Assert.Equal(ListLegalProcessesUseCase.DefaultPageSize, queries.PageSize);
    }

    [Fact]
    public async Task ExecuteAsync_WithMaximumPageSize_ForwardsBoundedMaximum()
    {
        var queries = new FakeLegalProcessReadQueries();
        ListLegalProcessesUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            queries);

        ListLegalProcessesResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            1,
            ListLegalProcessesUseCase.MaximumPageSize);

        Assert.Equal(
            ListLegalProcessesUseCase.MaximumPageSize,
            result.PageSize);
        Assert.Equal(
            ListLegalProcessesUseCase.MaximumPageSize,
            queries.PageSize);
    }

    [Theory]
    [InlineData(0, 20, "Page number")]
    [InlineData(-1, 20, "Page number")]
    [InlineData(1, 0, "Page size")]
    [InlineData(1, -1, "Page size")]
    [InlineData(1, 101, "Page size")]
    public async Task ExecuteAsync_WithInvalidPagination_RejectsBeforeAuthorizationOrQuery(
        int pageNumber,
        int pageSize,
        string expectedMessage)
    {
        var lookup = new StubOrganizationAccessLookup(OrganizationRole.Owner);
        var queries = new FakeLegalProcessReadQueries();
        ListLegalProcessesUseCase useCase = CreateUseCase(lookup, queries);

        RequestValidationException exception =
            await Assert.ThrowsAsync<RequestValidationException>(
                () => useCase.ExecuteAsync(
                    UserId,
                    OrganizationId,
                    pageNumber,
                    pageSize));

        Assert.Contains(expectedMessage, exception.Message);
        Assert.Equal(0, lookup.CallCount);
        Assert.Equal(0, queries.ListCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithCancellationToken_ForwardsTokenToQuery()
    {
        var queries = new FakeLegalProcessReadQueries();
        ListLegalProcessesUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            queries);
        using var cancellationTokenSource = new CancellationTokenSource();

        await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            1,
            20,
            cancellationTokenSource.Token);

        Assert.Equal(cancellationTokenSource.Token, queries.CancellationToken);
    }

    private static ListLegalProcessesUseCase CreateUseCase(
        OrganizationRole? role,
        FakeLegalProcessReadQueries queries)
    {
        return CreateUseCase(
            new StubOrganizationAccessLookup(role),
            queries);
    }

    private static ListLegalProcessesUseCase CreateUseCase(
        IOrganizationAccessLookup lookup,
        FakeLegalProcessReadQueries queries)
    {
        var actionAuthorization = new ProcessActionAuthorization(
            new OrganizationAccessAuthorization(lookup));

        return new ListLegalProcessesUseCase(actionAuthorization, queries);
    }

    private sealed class StubOrganizationAccessLookup(OrganizationRole? role)
        : IOrganizationAccessLookup
    {
        public int CallCount { get; private set; }

        public Task<OrganizationRole?> FindActiveRoleAsync(
            Guid userId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(role);
        }
    }

    private sealed class FakeLegalProcessReadQueries(
        IReadOnlyList<LegalProcessReadModel>? legalProcesses = null)
        : ILegalProcessReadQueries
    {
        public int ListCallCount { get; private set; }

        public Guid OrganizationId { get; private set; }

        public int PageNumber { get; private set; }

        public int PageSize { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public Task<LegalProcessReadModel?> FindAsync(
            Guid processId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException(
                "FindAsync must not be called by List Legal Processes tests.");
        }

        public Task<IReadOnlyList<LegalProcessReadModel>> ListAsync(
            Guid organizationId,
            int pageNumber,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            ListCallCount++;
            OrganizationId = organizationId;
            PageNumber = pageNumber;
            PageSize = pageSize;
            CancellationToken = cancellationToken;

            return Task.FromResult(
                legalProcesses ?? Array.Empty<LegalProcessReadModel>());
        }
    }
}
