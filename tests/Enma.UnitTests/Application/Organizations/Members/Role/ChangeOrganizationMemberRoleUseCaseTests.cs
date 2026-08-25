using Enma.Application.Authorization;
using Enma.Application.Organizations.Members.Role;
using Enma.Application.Validation;
using Enma.Domain.Organizations;

namespace Enma.UnitTests.Application.Organizations.Members.Role;

public sealed class ChangeOrganizationMemberRoleUseCaseTests
{
    private static readonly Guid UserId = Guid.Parse(
        "398c577c-3e40-4f6d-8b53-f6977307eaa0");
    private static readonly Guid OrganizationId = Guid.Parse(
        "5053370a-d7e8-4c41-a266-f110fe0f71c7");
    private static readonly Guid ActorMembershipId = Guid.Parse(
        "439683a2-c9c9-41e4-9bb9-6c27d531a146");
    private static readonly Guid TargetMembershipId = Guid.Parse(
        "6899124f-afeb-4ff0-b9f6-2374b35a1e71");

    [Theory]
    [InlineData("Administrator", "Member", OrganizationRole.Administrator,
        OrganizationRole.Member)]
    [InlineData("Member", "Administrator", OrganizationRole.Member,
        OrganizationRole.Administrator)]
    public async Task ExecuteAsync_OwnerChangingSupportedRole_Persists(
        string role,
        string expectedCurrentRole,
        OrganizationRole expectedRole,
        OrganizationRole expectedParsedCurrentRole)
    {
        var lookup = new MutableAccessLookup(OrganizationRole.Owner);
        var persistence = new RecordingPersistence(
            OrganizationMemberRoleMutationPersistenceResult.Succeeded);
        ChangeOrganizationMemberRoleUseCase useCase = Create(lookup, persistence);

        ChangeOrganizationMemberRoleResult result = await useCase.ExecuteAsync(
            CreateCommand(role, expectedCurrentRole));

        Assert.Equal(ChangeOrganizationMemberRoleResult.Succeeded, result);
        OrganizationMemberRoleMutationPersistenceRequest request = Assert.IsType<
            OrganizationMemberRoleMutationPersistenceRequest>(persistence.Request);
        Assert.Equal(UserId, request.UserId);
        Assert.Equal(OrganizationId, request.OrganizationId);
        Assert.Equal(ActorMembershipId, request.ActorMembershipId);
        Assert.Equal(TargetMembershipId, request.TargetMembershipId);
        Assert.Equal(expectedRole, request.Role);
        Assert.Equal(expectedParsedCurrentRole, request.ExpectedCurrentRole);
    }

    [Theory]
    [InlineData(OrganizationRole.Administrator)]
    [InlineData(OrganizationRole.Member)]
    public async Task ExecuteAsync_NonOwner_DeniesWithoutPersistence(
        OrganizationRole actorRole)
    {
        var lookup = new MutableAccessLookup(actorRole);
        var persistence = new RecordingPersistence(
            OrganizationMemberRoleMutationPersistenceResult.Succeeded);
        ChangeOrganizationMemberRoleUseCase useCase = Create(lookup, persistence);

        ChangeOrganizationMemberRoleResult result = await useCase.ExecuteAsync(
            CreateCommand("Administrator", "Member"));

        Assert.Equal(ChangeOrganizationMemberRoleResult.AccessDenied, result);
        Assert.Equal(0, persistence.CallCount);
    }

    [Theory]
    [InlineData(null, "Member")]
    [InlineData("", "Member")]
    [InlineData("Owner", "Member")]
    [InlineData("administrator", "Member")]
    [InlineData("Unsupported", "Member")]
    [InlineData("Administrator", null)]
    [InlineData("Administrator", "")]
    [InlineData("Administrator", "Owner")]
    [InlineData("Administrator", "member")]
    [InlineData("Administrator", "Unsupported")]
    public async Task ExecuteAsync_UnsupportedRoleInput_RejectsBeforeAuthorization(
        string? role,
        string? expectedCurrentRole)
    {
        var lookup = new MutableAccessLookup(OrganizationRole.Owner);
        var persistence = new RecordingPersistence(
            OrganizationMemberRoleMutationPersistenceResult.Succeeded);
        ChangeOrganizationMemberRoleUseCase useCase = Create(lookup, persistence);

        await Assert.ThrowsAsync<RequestValidationException>(() =>
            useCase.ExecuteAsync(CreateCommand(role, expectedCurrentRole)));

        Assert.Equal(0, lookup.CallCount);
        Assert.Equal(0, persistence.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyTargetMembership_ReturnsNotFoundWithoutPersistence()
    {
        var lookup = new MutableAccessLookup(OrganizationRole.Owner);
        var persistence = new RecordingPersistence(
            OrganizationMemberRoleMutationPersistenceResult.Succeeded);
        ChangeOrganizationMemberRoleUseCase useCase = Create(lookup, persistence);
        ChangeOrganizationMemberRoleCommand command = CreateCommand(
            "Administrator",
            "Member") with
        {
            MembershipId = Guid.Empty
        };

        ChangeOrganizationMemberRoleResult result = await useCase.ExecuteAsync(
            command);

        Assert.Equal(ChangeOrganizationMemberRoleResult.NotFound, result);
        Assert.Equal(0, persistence.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_LiveOwnerChangedToAdministrator_DeniesNextCall()
    {
        var lookup = new MutableAccessLookup(OrganizationRole.Owner);
        var persistence = new RecordingPersistence(
            OrganizationMemberRoleMutationPersistenceResult.Succeeded);
        ChangeOrganizationMemberRoleUseCase useCase = Create(lookup, persistence);

        ChangeOrganizationMemberRoleResult first = await useCase.ExecuteAsync(
            CreateCommand("Administrator", "Member"));
        lookup.Role = OrganizationRole.Administrator;
        ChangeOrganizationMemberRoleResult second = await useCase.ExecuteAsync(
            CreateCommand("Member", "Administrator"));

        Assert.Equal(ChangeOrganizationMemberRoleResult.Succeeded, first);
        Assert.Equal(ChangeOrganizationMemberRoleResult.AccessDenied, second);
        Assert.Equal(1, persistence.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_PropagatesCancellationToAuthorizationAndPersistence()
    {
        using var cancellationSource = new CancellationTokenSource();
        var lookup = new MutableAccessLookup(OrganizationRole.Owner);
        var persistence = new RecordingPersistence(
            OrganizationMemberRoleMutationPersistenceResult.Succeeded);
        ChangeOrganizationMemberRoleUseCase useCase = Create(lookup, persistence);

        await useCase.ExecuteAsync(
            CreateCommand("Administrator", "Member"),
            cancellationSource.Token);

        Assert.Equal(cancellationSource.Token, lookup.CancellationToken);
        Assert.Equal(cancellationSource.Token, persistence.CancellationToken);
    }

    private static ChangeOrganizationMemberRoleUseCase Create(
        MutableAccessLookup lookup,
        RecordingPersistence persistence)
    {
        return new ChangeOrganizationMemberRoleUseCase(
            new OrganizationAdministrationAuthorization(
                new OrganizationAccessAuthorization(lookup)),
            persistence);
    }

    private static ChangeOrganizationMemberRoleCommand CreateCommand(
        string? role,
        string? expectedCurrentRole)
    {
        return new ChangeOrganizationMemberRoleCommand(
            UserId,
            OrganizationId,
            TargetMembershipId,
            role,
            expectedCurrentRole);
    }

    private sealed class MutableAccessLookup(OrganizationRole role)
        : IOrganizationAccessLookup
    {
        public OrganizationRole Role { get; set; } = role;

        public int CallCount { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public Task<OrganizationRole?> FindActiveRoleAsync(
            Guid userId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<OrganizationRole?>(Role);
        }

        public Task<OrganizationAccessLookupResult?> FindActiveAccessAsync(
            Guid userId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            CancellationToken = cancellationToken;
            return Task.FromResult<OrganizationAccessLookupResult?>(new(
                UserId,
                OrganizationId,
                ActorMembershipId,
                Role));
        }
    }

    private sealed class RecordingPersistence(
        OrganizationMemberRoleMutationPersistenceResult result)
        : IOrganizationMemberRoleMutationPersistence
    {
        public int CallCount { get; private set; }

        public OrganizationMemberRoleMutationPersistenceRequest? Request
        {
            get;
            private set;
        }

        public CancellationToken CancellationToken { get; private set; }

        public Task<OrganizationMemberRoleMutationPersistenceResult> ExecuteAsync(
            OrganizationMemberRoleMutationPersistenceRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Request = request;
            CancellationToken = cancellationToken;
            return Task.FromResult(result);
        }
    }
}
