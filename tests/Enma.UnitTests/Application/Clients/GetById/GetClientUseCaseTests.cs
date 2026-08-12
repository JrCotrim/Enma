using Enma.Application.Authorization;
using Enma.Application.Clients;
using Enma.Application.Clients.GetById;
using Enma.Domain.Organizations;

namespace Enma.UnitTests.Application.Clients.GetById;

public sealed class GetClientUseCaseTests
{
    private static readonly Guid UserId = Guid.Parse(
        "cd6b0432-ceb5-4ba6-a603-62557dd12f46");

    private static readonly Guid OrganizationId = Guid.Parse(
        "135912d8-a278-4a76-a3b6-37b02a37959a");

    private static readonly Guid ClientId = Guid.Parse(
        "b7156d66-cdc7-4118-987a-10b14fd91f74");

    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        8,
        12,
        13,
        0,
        0,
        TimeSpan.Zero);

    [Fact]
    public async Task ExecuteAsync_WithDeniedView_DeniesWithoutClientQuery()
    {
        var queries = new FakeClientReadQueries();
        GetClientUseCase useCase = CreateUseCase(null, queries);

        GetClientResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            ClientId);

        Assert.Equal(GetClientResultStatus.AccessDenied, result.Status);
        Assert.Null(result.Client);
        Assert.Equal(0, queries.FindCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithMemberAndMatchingClient_ReturnsSafeReadModel()
    {
        var expectedClient = new ClientReadModel(
            ClientId,
            "Acme Legal",
            false,
            CreatedAt);
        var queries = new FakeClientReadQueries(expectedClient);
        GetClientUseCase useCase = CreateUseCase(OrganizationRole.Member, queries);

        GetClientResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            ClientId);

        Assert.Equal(GetClientResultStatus.Succeeded, result.Status);
        Assert.Equal(expectedClient, result.Client);
        Assert.Equal(ClientId, queries.ClientId);
        Assert.Equal(OrganizationId, queries.OrganizationId);
    }

    [Fact]
    public async Task ExecuteAsync_WithAuthorizedMissingClient_ReturnsNotFound()
    {
        var queries = new FakeClientReadQueries();
        GetClientUseCase useCase = CreateUseCase(OrganizationRole.Owner, queries);

        GetClientResult result = await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            ClientId);

        Assert.Equal(GetClientResultStatus.NotFound, result.Status);
        Assert.Null(result.Client);
        Assert.Equal(1, queries.FindCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithContext_ForwardsCancellationAndExactTenantPredicateInputs()
    {
        var queries = new FakeClientReadQueries();
        GetClientUseCase useCase = CreateUseCase(
            OrganizationRole.Administrator,
            queries);
        using var cancellationTokenSource = new CancellationTokenSource();

        await useCase.ExecuteAsync(
            UserId,
            OrganizationId,
            ClientId,
            cancellationTokenSource.Token);

        Assert.Equal(ClientId, queries.ClientId);
        Assert.Equal(OrganizationId, queries.OrganizationId);
        Assert.Equal(cancellationTokenSource.Token, queries.CancellationToken);
    }

    private static GetClientUseCase CreateUseCase(
        OrganizationRole? role,
        FakeClientReadQueries queries)
    {
        var actionAuthorization = new ClientActionAuthorization(
            new OrganizationAccessAuthorization(
                new StubOrganizationAccessLookup(role)));

        return new GetClientUseCase(actionAuthorization, queries);
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

    private sealed class FakeClientReadQueries(ClientReadModel? client = null)
        : IClientReadQueries
    {
        public int FindCallCount { get; private set; }

        public Guid ClientId { get; private set; }

        public Guid OrganizationId { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public Task<ClientReadModel?> FindAsync(
            Guid clientId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            FindCallCount++;
            ClientId = clientId;
            OrganizationId = organizationId;
            CancellationToken = cancellationToken;

            return Task.FromResult(client);
        }

        public Task<IReadOnlyList<ClientReadModel>> ListAsync(
            Guid organizationId,
            int pageNumber,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException(
                "ListAsync must not be called by Get Client tests.");
        }
    }
}
