using Enma.Application.Authorization;
using Enma.Application.Deadlines;
using Enma.Application.Deadlines.Responsible;
using Enma.Domain.Deadlines;
using Enma.Domain.Organizations;

namespace Enma.UnitTests.Application.Deadlines.Responsible;

public sealed class ChangeLegalDeadlineResponsibleUseCaseTests
{
    private static readonly Guid UserId = Guid.Parse(
        "b3e1c7a2-6d4f-4b8e-9a1c-0f2d5e7b9c31");
    private static readonly Guid OrganizationId = Guid.Parse(
        "d9a4f2c6-1e8b-4c3d-a7f0-6b2e9d1c4a85");
    private static readonly Guid MembershipId = Guid.Parse(
        "2c7e9a1f-4b3d-4f6a-8e0c-5d1b7a3f9e24");
    private static readonly Guid ProcessId = Guid.Parse(
        "6f3a8d2b-9c1e-4a7f-b5d0-3e8c2a6f1b97");
    private static readonly Guid DeadlineId = Guid.Parse(
        "8a1d5f3c-2e7b-4c9a-9f6d-0b4e8c2a7d51");
    private static readonly Guid ResponsibleMembershipId = Guid.Parse(
        "1e6b4d8a-3f2c-4a9e-b7d1-9c5a0f3e8b62");
    private static readonly Guid OtherResponsibleMembershipId = Guid.Parse(
        "f4c8a2e6-7b1d-4e3f-a9c5-2d6b0e8f4a13");
    private static readonly Guid ResponsibleUserId = Guid.Parse(
        "5b9e3a7c-0d2f-4c6e-8a4b-1f7d3c9e5a20");
    private static readonly DateTimeOffset CreatedAt = new(
        2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(OrganizationRole.Owner)]
    [InlineData(OrganizationRole.Administrator)]
    public async Task ExecuteAsync_WithAvailableResponsible_AssignsAndLocksRequestedMember(
        OrganizationRole role)
    {
        var persistence = new FakeMutationPersistence
        {
            RelatedMember = CreateMemberState(ResponsibleMembershipId, true, true)
        };
        ChangeLegalDeadlineResponsibleUseCase useCase = CreateUseCase(role, persistence);

        ChangeLegalDeadlineResponsibleResult result = await useCase.ExecuteAsync(
            CreateCommand(ResponsibleMembershipId));

        Assert.Equal(ChangeLegalDeadlineResponsibleResult.Succeeded, result);
        Assert.Equal(ResponsibleMembershipId, persistence.SelectedRelatedMembershipId);
        Assert.Equal(ResponsibleMembershipId, persistence.Deadline.ResponsibleMembershipId);
    }

    [Fact]
    public async Task ExecuteAsync_WithNull_ClearsWithoutLockingRelatedMember()
    {
        var persistence = new FakeMutationPersistence(ResponsibleMembershipId);
        ChangeLegalDeadlineResponsibleUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            persistence);

        ChangeLegalDeadlineResponsibleResult result = await useCase.ExecuteAsync(
            CreateCommand(null));

        Assert.Equal(ChangeLegalDeadlineResponsibleResult.Succeeded, result);
        Assert.Null(persistence.SelectedRelatedMembershipId);
        Assert.Null(persistence.Deadline.ResponsibleMembershipId);
    }

    [Fact]
    public async Task ExecuteAsync_WithCurrentResponsible_IsNoOpWithoutLockingRelatedMember()
    {
        var persistence = new FakeMutationPersistence(ResponsibleMembershipId);
        ChangeLegalDeadlineResponsibleUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            persistence);

        ChangeLegalDeadlineResponsibleResult result = await useCase.ExecuteAsync(
            CreateCommand(ResponsibleMembershipId));

        Assert.Equal(ChangeLegalDeadlineResponsibleResult.Succeeded, result);
        Assert.Null(persistence.SelectedRelatedMembershipId);
        Assert.Equal(ResponsibleMembershipId, persistence.Deadline.ResponsibleMembershipId);
    }

    [Fact]
    public async Task ExecuteAsync_WithCompletedDeadline_ChangesResponsibleAndKeepsCompletion()
    {
        var persistence = new FakeMutationPersistence(
            ResponsibleMembershipId,
            initiallyCompleted: true)
        {
            RelatedMember = CreateMemberState(OtherResponsibleMembershipId, true, true)
        };
        ChangeLegalDeadlineResponsibleUseCase useCase = CreateUseCase(
            OrganizationRole.Administrator,
            persistence);

        ChangeLegalDeadlineResponsibleResult result = await useCase.ExecuteAsync(
            CreateCommand(OtherResponsibleMembershipId));

        Assert.Equal(ChangeLegalDeadlineResponsibleResult.Succeeded, result);
        Assert.Equal(
            OtherResponsibleMembershipId,
            persistence.Deadline.ResponsibleMembershipId);
        Assert.NotNull(persistence.Deadline.CompletedAt);
    }

    [Theory]
    [InlineData("inactive-membership")]
    [InlineData("inactive-user")]
    [InlineData("foreign-organization")]
    [InlineData("missing")]
    public async Task ExecuteAsync_WithUnavailableResponsible_ReturnsNeutralResultWithoutMutation(
        string scenario)
    {
        var persistence = new FakeMutationPersistence(ResponsibleMembershipId)
        {
            RelatedMember = scenario switch
            {
                "inactive-membership" => CreateMemberState(
                    OtherResponsibleMembershipId,
                    false,
                    true),
                "inactive-user" => CreateMemberState(
                    OtherResponsibleMembershipId,
                    true,
                    false),
                "foreign-organization" => CreateMemberState(
                    OtherResponsibleMembershipId,
                    true,
                    true,
                    Guid.Parse("0c3f7e1a-9b5d-4a2c-8e6f-4d1b9a7c3e58")),
                _ => null
            }
        };
        ChangeLegalDeadlineResponsibleUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            persistence);

        ChangeLegalDeadlineResponsibleResult result = await useCase.ExecuteAsync(
            CreateCommand(OtherResponsibleMembershipId));

        Assert.Equal(
            ChangeLegalDeadlineResponsibleResult.RelatedResponsibleUnavailable,
            result);
        Assert.Equal(ResponsibleMembershipId, persistence.Deadline.ResponsibleMembershipId);
    }

    [Theory]
    [InlineData(OrganizationRole.Member)]
    [InlineData(null)]
    public async Task ExecuteAsync_WithoutUpdateAuthority_DeniesBeforePersistence(
        OrganizationRole? role)
    {
        var persistence = new FakeMutationPersistence();
        ChangeLegalDeadlineResponsibleUseCase useCase = CreateUseCase(role, persistence);

        ChangeLegalDeadlineResponsibleResult result = await useCase.ExecuteAsync(
            CreateCommand(ResponsibleMembershipId));

        Assert.Equal(ChangeLegalDeadlineResponsibleResult.AccessDenied, result);
        Assert.Equal(0, persistence.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithLockedActorDemoted_DeniesWithoutMutation()
    {
        var persistence = new FakeMutationPersistence
        {
            LockedActorRole = OrganizationRole.Member,
            RelatedMember = CreateMemberState(ResponsibleMembershipId, true, true)
        };
        ChangeLegalDeadlineResponsibleUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            persistence);

        ChangeLegalDeadlineResponsibleResult result = await useCase.ExecuteAsync(
            CreateCommand(ResponsibleMembershipId));

        Assert.Equal(ChangeLegalDeadlineResponsibleResult.AccessDenied, result);
        Assert.Null(persistence.Deadline.ResponsibleMembershipId);
    }

    [Fact]
    public async Task ExecuteAsync_WithEmptyDeadlineId_ReturnsNotFoundWithoutPersistence()
    {
        var persistence = new FakeMutationPersistence();
        ChangeLegalDeadlineResponsibleUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            persistence);

        ChangeLegalDeadlineResponsibleResult result = await useCase.ExecuteAsync(
            new ChangeLegalDeadlineResponsibleCommand(
                UserId,
                OrganizationId,
                Guid.Empty,
                ResponsibleMembershipId));

        Assert.Equal(ChangeLegalDeadlineResponsibleResult.NotFound, result);
        Assert.Equal(0, persistence.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithEmptyResponsibleId_ReturnsInvalidInputWithoutPersistence()
    {
        var persistence = new FakeMutationPersistence();
        ChangeLegalDeadlineResponsibleUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            persistence);

        ChangeLegalDeadlineResponsibleResult result = await useCase.ExecuteAsync(
            CreateCommand(Guid.Empty));

        Assert.Equal(ChangeLegalDeadlineResponsibleResult.InvalidInput, result);
        Assert.Equal(0, persistence.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WithMissingDeadline_ReturnsNotFound()
    {
        var persistence = new FakeMutationPersistence
        {
            Result = LegalDeadlineResponsibleMutationPersistenceResult.NotFound
        };
        ChangeLegalDeadlineResponsibleUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            persistence);

        ChangeLegalDeadlineResponsibleResult result = await useCase.ExecuteAsync(
            CreateCommand(ResponsibleMembershipId));

        Assert.Equal(ChangeLegalDeadlineResponsibleResult.NotFound, result);
    }

    [Fact]
    public async Task ExecuteAsync_ForwardsTenantDeadlineAndCancellation()
    {
        var persistence = new FakeMutationPersistence
        {
            RelatedMember = CreateMemberState(ResponsibleMembershipId, true, true)
        };
        ChangeLegalDeadlineResponsibleUseCase useCase = CreateUseCase(
            OrganizationRole.Owner,
            persistence);
        using var cancellation = new CancellationTokenSource();

        await useCase.ExecuteAsync(
            CreateCommand(ResponsibleMembershipId),
            cancellation.Token);

        Assert.Equal(OrganizationId, persistence.Request?.OrganizationId);
        Assert.Equal(DeadlineId, persistence.Request?.DeadlineId);
        Assert.Equal(MembershipId, persistence.Request?.ActorMembershipId);
        Assert.Equal(cancellation.Token, persistence.CancellationToken);
    }

    private static ChangeLegalDeadlineResponsibleCommand CreateCommand(
        Guid? responsibleMembershipId)
    {
        return new ChangeLegalDeadlineResponsibleCommand(
            UserId,
            OrganizationId,
            DeadlineId,
            responsibleMembershipId);
    }

    private static LegalDeadlineLockedActorState CreateMemberState(
        Guid membershipId,
        bool isMembershipActive,
        bool isUserActive,
        Guid? organizationId = null)
    {
        return new LegalDeadlineLockedActorState(
            membershipId,
            organizationId ?? OrganizationId,
            ResponsibleUserId,
            OrganizationRole.Member,
            isMembershipActive,
            isUserActive);
    }

    private static ChangeLegalDeadlineResponsibleUseCase CreateUseCase(
        OrganizationRole? role,
        FakeMutationPersistence persistence)
    {
        return new ChangeLegalDeadlineResponsibleUseCase(
            new DeadlineActionAuthorization(
                new OrganizationAccessAuthorization(
                    new StubOrganizationAccessLookup(role))),
            persistence);
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

        public Task<OrganizationAccessLookupResult?> FindActiveAccessAsync(
            Guid userId,
            Guid organizationId,
            CancellationToken cancellationToken = default)
        {
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

    private sealed class FakeMutationPersistence : ILegalDeadlineMutationPersistence
    {
        public FakeMutationPersistence(
            Guid? responsibleMembershipId = null,
            bool initiallyCompleted = false)
        {
            Deadline = new LegalDeadline(
                OrganizationId,
                ProcessId,
                "Initial title",
                new DateOnly(2026, 10, 15),
                CreatedAt,
                responsibleMembershipId);

            if (initiallyCompleted)
            {
                Deadline.Complete(CreatedAt.AddHours(1));
            }
        }

        public LegalDeadline Deadline { get; }
        public LegalDeadlineLockedActorState? RelatedMember { get; init; }
        public OrganizationRole LockedActorRole { get; init; } = OrganizationRole.Owner;
        public LegalDeadlineResponsibleMutationPersistenceResult? Result { get; init; }
        public Guid? SelectedRelatedMembershipId { get; private set; }
        public LegalDeadlineMutationPersistenceRequest? Request { get; private set; }
        public CancellationToken CancellationToken { get; private set; }
        public int CallCount { get; private set; }

        public Task<LegalDeadlineDetailsMutationPersistenceResult> UpdateDetailsAsync(
            LegalDeadlineMutationPersistenceRequest request,
            Func<LegalDeadlineMutationLockedState, LegalDeadlineMutationDecision> decide,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<LegalDeadlineLifecycleMutationPersistenceResult> CompleteAsync(
            LegalDeadlineMutationPersistenceRequest request,
            Func<LegalDeadlineMutationLockedState, LegalDeadlineMutationDecision> decide,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<LegalDeadlineLifecycleMutationPersistenceResult> ReopenAsync(
            LegalDeadlineMutationPersistenceRequest request,
            Func<LegalDeadline, Guid?> selectRelatedMembershipToLock,
            Func<LegalDeadlineMutationLockedState, LegalDeadlineMutationDecision> decide,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<LegalDeadlineResponsibleMutationPersistenceResult>
            ChangeResponsibleAsync(
                LegalDeadlineMutationPersistenceRequest request,
                Func<LegalDeadline, Guid?> selectRelatedMembershipToLock,
                Func<LegalDeadlineMutationLockedState, LegalDeadlineMutationDecision> decide,
                CancellationToken cancellationToken = default)
        {
            CallCount++;
            Request = request;
            CancellationToken = cancellationToken;

            if (Result is { } result)
            {
                return Task.FromResult(result);
            }

            SelectedRelatedMembershipId = selectRelatedMembershipToLock(Deadline);
            LegalDeadlineMutationDecision decision = decide(
                new LegalDeadlineMutationLockedState(
                    Deadline,
                    true,
                    new LegalDeadlineLockedActorState(
                        MembershipId,
                        request.OrganizationId,
                        request.UserId,
                        LockedActorRole,
                        true,
                        true),
                    SelectedRelatedMembershipId is null ? null : RelatedMember));

            return Task.FromResult(decision.Status switch
            {
                LegalDeadlineMutationDecisionStatus.AccessDenied =>
                    LegalDeadlineResponsibleMutationPersistenceResult.AccessDenied,
                LegalDeadlineMutationDecisionStatus.RelatedResponsibleUnavailable =>
                    LegalDeadlineResponsibleMutationPersistenceResult
                        .RelatedResponsibleUnavailable,
                _ => LegalDeadlineResponsibleMutationPersistenceResult.Succeeded
            });
        }
    }
}
