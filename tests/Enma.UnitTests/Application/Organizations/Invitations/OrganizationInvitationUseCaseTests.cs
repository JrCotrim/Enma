using Enma.Application.Authorization;
using Enma.Application.Organizations.Invitations;
using Enma.Application.Validation;
using Enma.Domain.Organizations;

namespace Enma.UnitTests.Application.Organizations.Invitations;

public sealed class OrganizationInvitationUseCaseTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid OrganizationId = Guid.NewGuid();
    private static readonly Guid MembershipId = Guid.NewGuid();
    private static readonly Guid InvitationId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(
        2026,
        8,
        30,
        12,
        0,
        0,
        TimeSpan.Zero);

    [Fact]
    public async Task Preview_UsableToken_ReturnsOnlyMaskedRecipientData()
    {
        var tokenService = new StubTokenService { HashSucceeds = true };
        var persistence = new StubMutationPersistence
        {
            PreviewResult = new PreviewOrganizationInvitationPersistenceResult(
                PreviewOrganizationInvitationPersistenceStatus.Usable,
                "Synthetic Legal",
                "member@example.test",
                OrganizationRole.Member)
        };
        var useCase = new PreviewOrganizationInvitationUseCase(
            tokenService,
            persistence);

        PreviewOrganizationInvitationResult result = await useCase.ExecuteAsync(
            StubTokenService.RawToken);

        Assert.Equal(PreviewOrganizationInvitationStatus.Usable, result.Status);
        Assert.Equal("Synthetic Legal", result.OrganizationName);
        Assert.Equal(OrganizationRole.Member, result.Role);
        Assert.Equal("m***@example.test", result.InvitedEmail);
        Assert.Equal(tokenService.TokenHash, persistence.PreviewTokenHash);
    }

    [Fact]
    public async Task Preview_InvalidToken_DoesNotQueryPersistence()
    {
        var persistence = new StubMutationPersistence();
        var useCase = new PreviewOrganizationInvitationUseCase(
            new StubTokenService(),
            persistence);

        PreviewOrganizationInvitationResult result = await useCase.ExecuteAsync(
            "malformed");

        Assert.Equal(PreviewOrganizationInvitationStatus.Invalid, result.Status);
        Assert.Null(persistence.PreviewTokenHash);
    }

    [Fact]
    public async Task Accept_ValidToken_UsesAuthenticatedUserAndMapsPersistence()
    {
        var tokenService = new StubTokenService { HashSucceeds = true };
        var persistence = new StubMutationPersistence
        {
            AcceptResult = AcceptOrganizationInvitationPersistenceResult.Succeeded
        };
        var useCase = new AcceptOrganizationInvitationUseCase(
            tokenService,
            persistence);

        AcceptOrganizationInvitationResult result = await useCase.ExecuteAsync(
            UserId,
            StubTokenService.RawToken);

        Assert.Equal(AcceptOrganizationInvitationResult.Succeeded, result);
        Assert.Equal(UserId, persistence.AcceptUserId);
        Assert.Equal(tokenService.TokenHash, persistence.AcceptTokenHash);
    }

    [Fact]
    public async Task Create_OwnerAdministratorInvite_NormalizesAndDeliversAfterPersistence()
    {
        var persistence = new StubMutationPersistence();
        var delivery = new StubDelivery();
        OrganizationInvitationDeliveryRequest deliveryRequest =
            CreateDeliveryRequest(OrganizationRole.Administrator);
        persistence.CreateResult = new CreateOrganizationInvitationPersistenceResult(
            CreateOrganizationInvitationPersistenceStatus.Succeeded,
            InvitationId,
            deliveryRequest);
        var useCase = new CreateOrganizationInvitationUseCase(
            CreateAuthorization(OrganizationRole.Owner),
            persistence,
            delivery);

        CreateOrganizationInvitationResult result = await useCase.ExecuteAsync(
            new CreateOrganizationInvitationCommand(
                UserId,
                OrganizationId,
                "  MEMBER@EXAMPLE.TEST ",
                "Administrator"));

        Assert.Equal(CreateOrganizationInvitationStatus.Succeeded, result.Status);
        Assert.Equal(InvitationId, result.InvitationId);
        Assert.Equal(
            OrganizationInvitationDeliveryResult.Accepted,
            result.DeliveryStatus);
        Assert.Equal("member@example.test", persistence.CreateRequest!.Email);
        Assert.Same(deliveryRequest, delivery.Request);
        Assert.True(persistence.CreateCompletedBeforeDelivery);
    }

    [Fact]
    public async Task Create_AdministratorCannotInviteAdministrator()
    {
        var persistence = new StubMutationPersistence();
        var delivery = new StubDelivery();
        var useCase = new CreateOrganizationInvitationUseCase(
            CreateAuthorization(OrganizationRole.Administrator),
            persistence,
            delivery);

        CreateOrganizationInvitationResult result = await useCase.ExecuteAsync(
            new CreateOrganizationInvitationCommand(
                UserId,
                OrganizationId,
                "member@example.test",
                "Administrator"));

        Assert.Equal(
            CreateOrganizationInvitationStatus.AccessDenied,
            result.Status);
        Assert.Null(persistence.CreateRequest);
        Assert.Null(delivery.Request);
    }

    [Fact]
    public async Task Create_DeliveryFailure_DoesNotChangeCommittedSuccess()
    {
        var persistence = new StubMutationPersistence
        {
            CreateResult = new CreateOrganizationInvitationPersistenceResult(
                CreateOrganizationInvitationPersistenceStatus.Succeeded,
                InvitationId,
                CreateDeliveryRequest(OrganizationRole.Member))
        };
        var delivery = new StubDelivery
        {
            Result = OrganizationInvitationDeliveryResult.Failed
        };
        var useCase = new CreateOrganizationInvitationUseCase(
            CreateAuthorization(OrganizationRole.Owner),
            persistence,
            delivery);

        CreateOrganizationInvitationResult result = await useCase.ExecuteAsync(
            new CreateOrganizationInvitationCommand(
                UserId,
                OrganizationId,
                "member@example.test",
                "Member"));

        Assert.Equal(CreateOrganizationInvitationStatus.Succeeded, result.Status);
        Assert.Equal(
            OrganizationInvitationDeliveryResult.Failed,
            result.DeliveryStatus);
    }

    [Fact]
    public async Task Create_PersistenceFailure_DoesNotAttemptDelivery()
    {
        var persistence = new StubMutationPersistence
        {
            CreateException = new InvalidOperationException("synthetic database failure")
        };
        var delivery = new StubDelivery();
        var useCase = new CreateOrganizationInvitationUseCase(
            CreateAuthorization(OrganizationRole.Owner),
            persistence,
            delivery);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            useCase.ExecuteAsync(new CreateOrganizationInvitationCommand(
                UserId,
                OrganizationId,
                "member@example.test",
                "Member")));

        Assert.Null(delivery.Request);
    }

    [Fact]
    public async Task Resend_AdministratorCannotManageAdministratorInvitation()
    {
        var persistence = new StubMutationPersistence();
        var delivery = new StubDelivery();
        var queries = new StubReadQueries
        {
            Role = OrganizationRole.Administrator
        };
        var useCase = new OrganizationInvitationLifecycleUseCase(
            CreateAuthorization(OrganizationRole.Administrator),
            queries,
            persistence,
            delivery);

        OrganizationInvitationLifecycleResult result = await useCase.ResendAsync(
            UserId,
            OrganizationId,
            InvitationId);

        Assert.Equal(
            OrganizationInvitationLifecycleStatus.AccessDenied,
            result.Status);
        Assert.Null(persistence.LifecycleRequest);
        Assert.Null(delivery.Request);
    }

    [Fact]
    public async Task List_InvalidPagination_FailsBeforeAuthorizationOrQuery()
    {
        var queries = new StubReadQueries();
        var access = new StubAccessLookup(OrganizationRole.Owner);
        var useCase = new ListOrganizationInvitationsUseCase(
            CreateAuthorization(access),
            queries,
            new FixedTimeProvider(Now));

        await Assert.ThrowsAsync<RequestValidationException>(() =>
            useCase.ExecuteAsync(
                UserId,
                OrganizationId,
                pageNumber: 1,
                pageSize: 101));

        Assert.Equal(0, access.CallCount);
        Assert.Null(queries.Query);
    }

    private static OrganizationAdministrationAuthorization CreateAuthorization(
        OrganizationRole role)
    {
        return CreateAuthorization(new StubAccessLookup(role));
    }

    private static OrganizationAdministrationAuthorization CreateAuthorization(
        StubAccessLookup access)
    {
        return new OrganizationAdministrationAuthorization(
            new OrganizationAccessAuthorization(access));
    }

    private static OrganizationInvitationDeliveryRequest CreateDeliveryRequest(
        OrganizationRole role)
    {
        return new OrganizationInvitationDeliveryRequest(
            "member@example.test",
            "Synthetic Legal",
            role,
            Now.AddDays(7),
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmno-_");
    }

    private sealed class StubAccessLookup(OrganizationRole role)
        : IOrganizationAccessLookup
    {
        public int CallCount { get; private set; }

        public Task<OrganizationRole?> FindActiveRoleAsync(
            Guid userId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult<OrganizationRole?>(role);
        }

        public Task<OrganizationAccessLookupResult?> FindActiveAccessAsync(
            Guid userId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult<OrganizationAccessLookupResult?>(new(
                userId,
                organizationId,
                MembershipId,
                role));
        }
    }

    private sealed class StubMutationPersistence
        : IOrganizationInvitationMutationPersistence
    {
        public PreviewOrganizationInvitationPersistenceResult PreviewResult
            { get; init; } = new(
                PreviewOrganizationInvitationPersistenceStatus.Invalid);

        public AcceptOrganizationInvitationPersistenceResult AcceptResult
            { get; init; } =
                AcceptOrganizationInvitationPersistenceResult.Rejected;

        public OrganizationInvitationTokenHash? PreviewTokenHash
            { get; private set; }

        public Guid? AcceptUserId { get; private set; }

        public OrganizationInvitationTokenHash? AcceptTokenHash
            { get; private set; }

        public CreateOrganizationInvitationPersistenceResult CreateResult { get; set; } =
            new(CreateOrganizationInvitationPersistenceStatus.AccessDenied);

        public CreateOrganizationInvitationPersistenceRequest? CreateRequest
            { get; private set; }

        public Exception? CreateException { get; init; }

        public OrganizationInvitationMutationPersistenceRequest? LifecycleRequest
            { get; private set; }

        public bool CreateCompletedBeforeDelivery { get; private set; }

        public Task<PreviewOrganizationInvitationPersistenceResult> PreviewAsync(
            OrganizationInvitationTokenHash tokenHash,
            CancellationToken cancellationToken = default)
        {
            PreviewTokenHash = tokenHash;
            return Task.FromResult(PreviewResult);
        }

        public Task<AcceptOrganizationInvitationPersistenceResult> AcceptAsync(
            Guid userId,
            OrganizationInvitationTokenHash tokenHash,
            CancellationToken cancellationToken = default)
        {
            AcceptUserId = userId;
            AcceptTokenHash = tokenHash;
            return Task.FromResult(AcceptResult);
        }

        public Task<CreateOrganizationInvitationPersistenceResult> CreateAsync(
            CreateOrganizationInvitationPersistenceRequest request,
            CancellationToken cancellationToken = default)
        {
            CreateRequest = request;

            if (CreateException is not null)
            {
                throw CreateException;
            }

            CreateCompletedBeforeDelivery = true;
            return Task.FromResult(CreateResult);
        }

        public Task<RevokeOrganizationInvitationPersistenceResult> RevokeAsync(
            OrganizationInvitationMutationPersistenceRequest request,
            CancellationToken cancellationToken = default)
        {
            LifecycleRequest = request;
            return Task.FromResult(
                RevokeOrganizationInvitationPersistenceResult.Succeeded);
        }

        public Task<ResendOrganizationInvitationPersistenceResult> ResendAsync(
            OrganizationInvitationMutationPersistenceRequest request,
            CancellationToken cancellationToken = default)
        {
            LifecycleRequest = request;
            return Task.FromResult(new ResendOrganizationInvitationPersistenceResult(
                ResendOrganizationInvitationPersistenceStatus.Succeeded,
                CreateDeliveryRequest(OrganizationRole.Member)));
        }
    }

    private sealed class StubDelivery : IOrganizationInvitationDelivery
    {
        public OrganizationInvitationDeliveryResult Result { get; set; } =
            OrganizationInvitationDeliveryResult.Accepted;

        public OrganizationInvitationDeliveryRequest? Request { get; private set; }

        public Task<OrganizationInvitationDeliveryResult> DeliverAsync(
            OrganizationInvitationDeliveryRequest request,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            return Task.FromResult(Result);
        }
    }

    private sealed class StubReadQueries : IOrganizationInvitationReadQueries
    {
        public OrganizationRole? Role { get; init; }

        public OrganizationInvitationQuery? Query { get; private set; }

        public Task<OrganizationInvitationPage> ListAsync(
            OrganizationInvitationQuery query,
            CancellationToken cancellationToken = default)
        {
            Query = query;
            return Task.FromResult(new OrganizationInvitationPage([], 0));
        }

        public Task<OrganizationRole?> FindRoleAsync(
            Guid organizationId,
            Guid invitationId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Role);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class StubTokenService : IOrganizationInvitationTokenService
    {
        internal const string RawToken =
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmno-_";

        public OrganizationInvitationTokenHash TokenHash { get; } =
            new(Enumerable.Repeat((byte)1, 32).ToArray());

        public bool HashSucceeds { get; init; }

        public string GenerateToken(out OrganizationInvitationTokenHash tokenHash)
        {
            tokenHash = TokenHash;
            return RawToken;
        }

        public bool TryHashToken(
            string? rawToken,
            out OrganizationInvitationTokenHash? tokenHash)
        {
            tokenHash = HashSucceeds ? TokenHash : null;
            return HashSucceeds;
        }
    }
}
