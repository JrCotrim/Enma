using Enma.Application.Authorization;
using Enma.Application.Deadlines;
using Enma.Application.Deadlines.GetById;
using Enma.Domain.Organizations;

namespace Enma.UnitTests.Application.Deadlines.GetById;

public sealed class GetLegalDeadlineUseCaseTests
{
    private static readonly Guid UserId = Guid.Parse(
        "25813db8-5fdd-45e0-919d-b5169b37e3d6");

    private static readonly Guid OrganizationId = Guid.Parse(
        "fd8aa1f2-4394-42b5-b2c2-0c5be5ef2d18");

    private static readonly Guid DeadlineId = Guid.Parse(
        "55bf00fd-c690-413b-be43-e1e306564d41");

    private static readonly Guid ProcessId = Guid.Parse(
        "e69c789a-d6b1-4344-9895-8eb62011770c");

    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        8,
        13,
        16,
        0,
        0,
        TimeSpan.Zero);

    [Fact]
    public async Task ExecuteAsync_WithDeniedView_DeniesWithoutDeadlineQuery()
    {
        var queries = new FakeDeadlineReadQueries();
        GetLegalDeadlineUseCase useCase = CreateUseCase(null, queries);

        GetLegalDeadlineResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            DeadlineId);

        Assert.Same(GetLegalDeadlineResult.AccessDenied, result);
        Assert.Equal(0, queries.FindCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithMemberAndMatchingDeadline_ReturnsApprovedDetail()
    {
        var expectedDeadline = new LegalDeadlineDetailReadModel(
            DeadlineId,
            "File Appellate Brief",
            new DateOnly(2026, 9, 15),
            ProcessId,
            "Appellate Matter",
            "Acme Legal",
            LegalDeadlineReadState.Pending,
            CreatedAt,
            null);
        var queries = new FakeDeadlineReadQueries(expectedDeadline);
        GetLegalDeadlineUseCase useCase = CreateUseCase(
            OrganizationRole.Member,
            queries);

        GetLegalDeadlineResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            DeadlineId);

        Assert.Equal(GetLegalDeadlineResultStatus.Succeeded, result.Status);
        Assert.Equal(expectedDeadline, result.LegalDeadline);
        Assert.Equal(DeadlineId, queries.DeadlineId);
        Assert.Equal(OrganizationId, queries.OrganizationId);
    }

    [Fact]
    public async Task ExecuteAsync_WithMissingOrCrossTenantDeadline_ReturnsSameNotFoundResult()
    {
        GetLegalDeadlineUseCase missingUseCase = CreateUseCase(
            OrganizationRole.Owner,
            new FakeDeadlineReadQueries());
        GetLegalDeadlineUseCase crossTenantUseCase = CreateUseCase(
            OrganizationRole.Owner,
            new FakeDeadlineReadQueries());

        GetLegalDeadlineResult missing = await missingUseCase.ExecuteAsync(
            UserId,
            OrganizationId,
            Guid.NewGuid());
        GetLegalDeadlineResult crossTenant = await crossTenantUseCase.ExecuteAsync(
            UserId,
            OrganizationId,
            DeadlineId);

        Assert.Same(GetLegalDeadlineResult.NotFound, missing);
        Assert.Same(missing, crossTenant);
    }

    [Fact]
    public async Task ExecuteAsync_WithEmptyDeadlineId_ReturnsNotFoundWithoutQuery()
    {
        var queries = new FakeDeadlineReadQueries();
        GetLegalDeadlineUseCase useCase = CreateUseCase(
            OrganizationRole.Administrator,
            queries);

        GetLegalDeadlineResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            Guid.Empty);

        Assert.Same(GetLegalDeadlineResult.NotFound, result);
        Assert.Equal(0, queries.FindCallCount);
    }

    [Fact]
    public void DetailReadContract_ContainsOnlyApprovedFieldsAndStates()
    {
        Assert.Equal(
            [
                nameof(LegalDeadlineDetailReadModel.Id),
                nameof(LegalDeadlineDetailReadModel.Title),
                nameof(LegalDeadlineDetailReadModel.DueDate),
                nameof(LegalDeadlineDetailReadModel.ProcessId),
                nameof(LegalDeadlineDetailReadModel.ProcessTitle),
                nameof(LegalDeadlineDetailReadModel.ClientName),
                nameof(LegalDeadlineDetailReadModel.State),
                nameof(LegalDeadlineDetailReadModel.CreatedAt),
                nameof(LegalDeadlineDetailReadModel.CompletedAt)
            ],
            typeof(LegalDeadlineDetailReadModel)
                .GetProperties()
                .Select(property => property.Name));
        Assert.Equal(
            [
                nameof(LegalDeadlineReadState.Pending),
                nameof(LegalDeadlineReadState.Completed)
            ],
            Enum.GetNames<LegalDeadlineReadState>());
    }

    [Fact]
    public async Task ExecuteAsync_WithContext_ForwardsExactTenantScopeAndCancellation()
    {
        var queries = new FakeDeadlineReadQueries();
        GetLegalDeadlineUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            queries);
        using var cancellationTokenSource = new CancellationTokenSource();

        await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            DeadlineId,
            cancellationTokenSource.Token);

        Assert.Equal(DeadlineId, queries.DeadlineId);
        Assert.Equal(OrganizationId, queries.OrganizationId);
        Assert.Equal(cancellationTokenSource.Token, queries.CancellationToken);
    }

    private static GetLegalDeadlineUseCase CreateUseCase(
        OrganizationRole? role,
        FakeDeadlineReadQueries queries)
    {
        var authorization = new DeadlineActionAuthorization(
            new OrganizationAccessAuthorization(
                new StubOrganizationAccessLookup(role)));
        return new GetLegalDeadlineUseCase(authorization, queries);
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

    private sealed class FakeDeadlineReadQueries(
        LegalDeadlineDetailReadModel? legalDeadline = null)
        : ILegalDeadlineReadQueries
    {
        public int FindCallCount { get; private set; }

        public Guid DeadlineId { get; private set; }

        public Guid OrganizationId { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public Task<LegalDeadlineDetailReadModel?> FindAsync(
            Guid deadlineId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            FindCallCount++;
            DeadlineId = deadlineId;
            OrganizationId = organizationId;
            CancellationToken = cancellationToken;
            return Task.FromResult(legalDeadline);
        }

        public Task<IReadOnlyList<LegalDeadlineListItem>> ListAsync(
            Guid organizationId,
            int pageNumber,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException(
                "ListAsync must not be called by Get Deadline tests.");
        }
    }
}
