using Enma.Application.Authorization;
using Enma.Application.Processes;
using Enma.Application.Processes.GetById;
using Enma.Domain.Organizations;

namespace Enma.UnitTests.Application.Processes.GetById;

public sealed class GetLegalProcessUseCaseTests
{
    private static readonly Guid UserId = Guid.Parse(
        "b79a731c-5562-45cc-9f46-04a15236c784");

    private static readonly Guid OrganizationId = Guid.Parse(
        "a3347575-aaf4-499f-a69b-b487228ea45d");

    private static readonly Guid ProcessId = Guid.Parse(
        "81660986-0394-4a70-9c41-d6ce04bf2408");

    private static readonly Guid ClientId = Guid.Parse(
        "eb8f7135-dd15-4881-b206-a0e8c9092309");

    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        8,
        13,
        13,
        0,
        0,
        TimeSpan.Zero);

    [Fact]
    public async Task ExecuteAsync_WithDeniedView_DeniesWithoutProcessQuery()
    {
        var queries = new FakeLegalProcessReadQueries();
        GetLegalProcessUseCase useCase = CreateUseCase(null, queries);

        GetLegalProcessResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            ProcessId);

        Assert.Equal(GetLegalProcessResultStatus.AccessDenied, result.Status);
        Assert.Null(result.LegalProcess);
        Assert.Equal(0, queries.FindCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithMemberAndMatchingProcess_ReturnsApprovedReadModel()
    {
        var expectedProcess = new LegalProcessReadModel(
            ProcessId,
            "Contract Review",
            ClientId,
            "Acme Legal",
            CreatedAt);
        var queries = new FakeLegalProcessReadQueries(expectedProcess);
        GetLegalProcessUseCase useCase = CreateUseCase(
            OrganizationRole.Member,
            queries);

        GetLegalProcessResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            ProcessId);

        Assert.Equal(GetLegalProcessResultStatus.Succeeded, result.Status);
        Assert.Equal(expectedProcess, result.LegalProcess);
        Assert.Equal(ProcessId, queries.ProcessId);
        Assert.Equal(OrganizationId, queries.OrganizationId);
    }

    [Fact]
    public async Task ExecuteAsync_WithMissingOrCrossTenantProcess_ReturnsSameNotFoundResult()
    {
        GetLegalProcessUseCase missingUseCase = CreateUseCase(
            OrganizationRole.Owner,
            new FakeLegalProcessReadQueries());
        GetLegalProcessUseCase crossTenantUseCase = CreateUseCase(
            OrganizationRole.Owner,
            new FakeLegalProcessReadQueries());

        GetLegalProcessResult missingResult = await missingUseCase.ExecuteAsync(
            UserId,
            OrganizationId,
            Guid.NewGuid());
        GetLegalProcessResult crossTenantResult = await crossTenantUseCase.ExecuteAsync(
            UserId,
            OrganizationId,
            ProcessId);

        Assert.Same(GetLegalProcessResult.NotFound, missingResult);
        Assert.Same(missingResult, crossTenantResult);
    }

    [Fact]
    public async Task ExecuteAsync_WithEmptyProcessId_ReturnsNotFoundWithoutQuery()
    {
        var queries = new FakeLegalProcessReadQueries();
        GetLegalProcessUseCase useCase = CreateUseCase(
            OrganizationRole.Administrator,
            queries);

        GetLegalProcessResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            Guid.Empty);

        Assert.Same(GetLegalProcessResult.NotFound, result);
        Assert.Equal(0, queries.FindCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithContext_ForwardsExactTenantScopeAndCancellation()
    {
        var queries = new FakeLegalProcessReadQueries();
        GetLegalProcessUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            queries);
        using var cancellationTokenSource = new CancellationTokenSource();

        await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            ProcessId,
            cancellationTokenSource.Token);

        Assert.Equal(ProcessId, queries.ProcessId);
        Assert.Equal(OrganizationId, queries.OrganizationId);
        Assert.Equal(cancellationTokenSource.Token, queries.CancellationToken);
    }

    private static GetLegalProcessUseCase CreateUseCase(
        OrganizationRole? role,
        FakeLegalProcessReadQueries queries)
    {
        var actionAuthorization = new ProcessActionAuthorization(
            new OrganizationAccessAuthorization(
                new StubOrganizationAccessLookup(role)));

        return new GetLegalProcessUseCase(actionAuthorization, queries);
    }

    private sealed class StubOrganizationAccessLookup(OrganizationRole? role)
        : IOrganizationAccessLookup
    {
        public Task<OrganizationRole?> FindActiveRoleAsync(
            Guid userId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(role);
        }
    }

    private sealed class FakeLegalProcessReadQueries(
        LegalProcessReadModel? legalProcess = null) : ILegalProcessReadQueries
    {
        public int FindCallCount { get; private set; }

        public Guid ProcessId { get; private set; }

        public Guid OrganizationId { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public Task<LegalProcessReadModel?> FindAsync(
            Guid processId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            FindCallCount++;
            ProcessId = processId;
            OrganizationId = organizationId;
            CancellationToken = cancellationToken;

            return Task.FromResult(legalProcess);
        }

        public Task<IReadOnlyList<LegalProcessReadModel>> ListAsync(
            Guid organizationId,
            int pageNumber,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException(
                "ListAsync must not be called by Get Legal Process tests.");
        }
    }
}
