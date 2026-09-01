using System.Data;
using System.Data.Common;
using Enma.Application.Clients;
using Enma.Application.Organizations.Invitations;
using Enma.Application.Organizations.Members.Lifecycle;
using Enma.Application.Organizations.Members.Role;
using Enma.Application.Tasks;
using Enma.Domain.Auditing;
using Enma.Domain.Clients;
using Enma.Domain.Organizations;
using Enma.Domain.Tasks;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Enma.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Enma.IntegrationTests.Infrastructure.Persistence;

[Collection(PostgreSqlCollection.Name)]
public sealed class OrganizationInvitationMutationPersistenceConcurrencyTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        8,
        30,
        9,
        0,
        0,
        TimeSpan.Zero);
    private static readonly DateTimeOffset Now = CreatedAt.AddHours(2);

    public Task InitializeAsync() => fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task CreateCreate_SameOrganizationEmail_SerializesToOneInvitation()
    {
        TestGraph graph = await SeedGraphAsync(OrganizationRole.Owner);
        var pause = new PauseAfterOrganizationLockInterceptor();
        CreateOrganizationInvitationPersistenceRequest request = CreateRequest(
            graph,
            "race-create@example.test",
            OrganizationRole.Member);
        using var timeout = CreateTimeout();

        Task<CreateOrganizationInvitationPersistenceResult> first =
            CreatePersistence(pause).CreateAsync(request, timeout.Token);
        await pause.LockAcquired.WaitAsync(timeout.Token);
        Task<CreateOrganizationInvitationPersistenceResult> second =
            CreatePersistence().CreateAsync(request, timeout.Token);

        pause.Release();
        CreateOrganizationInvitationPersistenceResult[] results =
            await Task.WhenAll(first, second).WaitAsync(timeout.Token);

        Assert.Contains(
            results,
            result => result.Status ==
                CreateOrganizationInvitationPersistenceStatus.Succeeded);
        Assert.Contains(
            results,
            result => result.Status ==
                CreateOrganizationInvitationPersistenceStatus
                    .DuplicatePendingInvitation);
        await AssertDatabaseStateAsync(
            invitationCount: 1,
            AuditEventType.OrganizationInvitationCreated,
            expectedAuditCount: 1);
    }

    [Fact]
    public async Task RevokeResend_RevokeFirstMakesResendConflict()
    {
        TestGraph graph = await SeedGraphAsync(OrganizationRole.Owner);
        OrganizationInvitation invitation = await SeedInvitationAsync(
            graph,
            OrganizationRole.Administrator);
        var pause = new PauseAfterOrganizationLockInterceptor();
        OrganizationInvitationMutationPersistenceRequest request =
            LifecycleRequest(graph, invitation.Id);
        using var timeout = CreateTimeout();

        Task<RevokeOrganizationInvitationPersistenceResult> revoke =
            CreatePersistence(pause).RevokeAsync(request, timeout.Token);
        await pause.LockAcquired.WaitAsync(timeout.Token);
        Task<ResendOrganizationInvitationPersistenceResult> resend =
            CreatePersistence().ResendAsync(request, timeout.Token);

        pause.Release();
        await Task.WhenAll(revoke, resend).WaitAsync(timeout.Token);

        Assert.Equal(
            RevokeOrganizationInvitationPersistenceResult.Succeeded,
            await revoke);
        Assert.Equal(
            ResendOrganizationInvitationPersistenceStatus.Conflict,
            (await resend).Status);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        OrganizationInvitation stored = await dbContext
            .OrganizationInvitations
            .SingleAsync();
        Assert.Equal(OrganizationInvitationState.Revoked, stored.GetState(Now));
        Assert.Equal(
            [AuditEventType.OrganizationInvitationRevoked],
            await dbContext.AuditLogs
                .Select(audit => audit.EventType)
                .ToArrayAsync());
    }

    [Fact]
    public async Task ResendResend_SerializesToOneRotationAndOldTokenIsInvalid()
    {
        TestGraph graph = await SeedGraphAsync(OrganizationRole.Owner);
        var tokenService = new CryptographicOrganizationInvitationTokenService();
        string oldRawToken = tokenService.GenerateToken(out var oldHash);
        OrganizationInvitation invitation = await SeedInvitationAsync(
            graph,
            OrganizationRole.Administrator,
            oldHash);
        var pause = new PauseAfterOrganizationLockInterceptor();
        OrganizationInvitationMutationPersistenceRequest request =
            LifecycleRequest(graph, invitation.Id);
        using var timeout = CreateTimeout();

        Task<ResendOrganizationInvitationPersistenceResult> first =
            CreatePersistence(pause).ResendAsync(request, timeout.Token);
        await pause.LockAcquired.WaitAsync(timeout.Token);
        Task<ResendOrganizationInvitationPersistenceResult> second =
            CreatePersistence().ResendAsync(request, timeout.Token);

        pause.Release();
        ResendOrganizationInvitationPersistenceResult[] results =
            await Task.WhenAll(first, second).WaitAsync(timeout.Token);

        ResendOrganizationInvitationPersistenceResult succeeded = Assert.Single(
            results,
            result => result.Status ==
                ResendOrganizationInvitationPersistenceStatus.Succeeded);
        Assert.Single(
            results,
            result => result.Status ==
                ResendOrganizationInvitationPersistenceStatus.Cooldown);
        Assert.True(tokenService.TryHashToken(
            succeeded.DeliveryRequest!.RawToken,
            out var newHash));
        Assert.True(tokenService.TryHashToken(oldRawToken, out var rehashedOldToken));

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        OrganizationInvitation stored = await dbContext
            .OrganizationInvitations
            .SingleAsync();
        Assert.Equal(newHash, stored.TokenHash);
        Assert.NotEqual(rehashedOldToken, stored.TokenHash);
        Assert.Equal(1, await dbContext.AuditLogs.CountAsync(audit =>
            audit.EventType == AuditEventType.OrganizationInvitationResent));
    }

    [Fact]
    public async Task RevokeRevoke_IsIdempotentWithOneAudit()
    {
        TestGraph graph = await SeedGraphAsync(OrganizationRole.Administrator);
        OrganizationInvitation invitation = await SeedInvitationAsync(
            graph,
            OrganizationRole.Member);
        var pause = new PauseAfterOrganizationLockInterceptor();
        OrganizationInvitationMutationPersistenceRequest request =
            LifecycleRequest(graph, invitation.Id);
        using var timeout = CreateTimeout();

        Task<RevokeOrganizationInvitationPersistenceResult> first =
            CreatePersistence(pause).RevokeAsync(request, timeout.Token);
        await pause.LockAcquired.WaitAsync(timeout.Token);
        Task<RevokeOrganizationInvitationPersistenceResult> second =
            CreatePersistence().RevokeAsync(request, timeout.Token);

        pause.Release();
        RevokeOrganizationInvitationPersistenceResult[] results =
            await Task.WhenAll(first, second).WaitAsync(timeout.Token);

        Assert.All(
            results,
            result => Assert.Equal(
                RevokeOrganizationInvitationPersistenceResult.Succeeded,
                result));
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Equal(1, await dbContext.AuditLogs.CountAsync(audit =>
            audit.EventType == AuditEventType.OrganizationInvitationRevoked));
    }

    [Fact]
    public async Task ExpirationMaterializationCreate_RaceKeepsOneOpenInvitation()
    {
        TestGraph graph = await SeedGraphAsync(OrganizationRole.Owner);
        var expired = new OrganizationInvitation(
            graph.Organization.Id,
            "expired-race@example.test",
            OrganizationRole.Member,
            graph.Membership.Id,
            RandomHash(),
            Now.AddDays(-8),
            Now.AddDays(-8),
            Now.AddDays(-1));
        await SeedAsync(expired);
        var pause = new PauseAfterOrganizationLockInterceptor();
        CreateOrganizationInvitationPersistenceRequest request = CreateRequest(
            graph,
            expired.InvitedEmail,
            OrganizationRole.Member);
        using var timeout = CreateTimeout();

        Task<CreateOrganizationInvitationPersistenceResult> first =
            CreatePersistence(pause).CreateAsync(request, timeout.Token);
        await pause.LockAcquired.WaitAsync(timeout.Token);
        Task<CreateOrganizationInvitationPersistenceResult> second =
            CreatePersistence().CreateAsync(request, timeout.Token);

        pause.Release();
        CreateOrganizationInvitationPersistenceResult[] results =
            await Task.WhenAll(first, second).WaitAsync(timeout.Token);

        Assert.Single(
            results,
            result => result.Status ==
                CreateOrganizationInvitationPersistenceStatus.Succeeded);
        Assert.Single(
            results,
            result => result.Status ==
                CreateOrganizationInvitationPersistenceStatus
                    .DuplicatePendingInvitation);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        OrganizationInvitation[] invitations = await dbContext
            .OrganizationInvitations
            .OrderBy(invitation => invitation.CreatedAt)
            .ToArrayAsync();
        Assert.Equal(2, invitations.Length);
        Assert.Equal(expired.ExpiresAt, invitations[0].ExpiredAt);
        Assert.Equal(OrganizationInvitationState.Pending, invitations[1].GetState(Now));
    }

    [Fact]
    public async Task RoleChangeCreate_RoleChangeFirstMakesStaleActorLose()
    {
        AdministrationGraph graph = await SeedAdministrationGraphAsync();
        var pause = new PauseAfterOrganizationLockInterceptor();
        var rolePersistence = new OrganizationMemberRoleMutationPersistence(
            CreateOptions(pause),
            new FixedTimeProvider(Now));
        var roleRequest = new OrganizationMemberRoleMutationPersistenceRequest(
            graph.Owner.Id,
            graph.Organization.Id,
            graph.OwnerMembership.Id,
            graph.AdministratorMembership.Id,
            OrganizationRole.Member,
            OrganizationRole.Administrator);
        CreateOrganizationInvitationPersistenceRequest createRequest = new(
            graph.Administrator.Id,
            graph.Organization.Id,
            graph.AdministratorMembership.Id,
            "stale-role@example.test",
            OrganizationRole.Member);
        using var timeout = CreateTimeout();

        Task<OrganizationMemberRoleMutationPersistenceResult> roleChange =
            rolePersistence.ExecuteAsync(roleRequest, timeout.Token);
        await pause.LockAcquired.WaitAsync(timeout.Token);
        Task<CreateOrganizationInvitationPersistenceResult> create =
            CreatePersistence().CreateAsync(createRequest, timeout.Token);

        pause.Release();
        await Task.WhenAll(roleChange, create).WaitAsync(timeout.Token);

        Assert.Equal(
            OrganizationMemberRoleMutationPersistenceResult.Succeeded,
            await roleChange);
        Assert.Equal(
            CreateOrganizationInvitationPersistenceStatus.AccessDenied,
            (await create).Status);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Equal(0, await dbContext.OrganizationInvitations.CountAsync());
        Assert.Equal(
            [AuditEventType.OrganizationMembershipRoleChanged],
            await dbContext.AuditLogs
                .Select(audit => audit.EventType)
                .ToArrayAsync());
    }

    [Fact]
    public Task RoleChangeRevoke_RoleChangeFirstMakesStaleActorLose()
    {
        return ExecuteRoleChangeLifecycleRaceAsync(resend: false);
    }

    [Fact]
    public Task RoleChangeResend_RoleChangeFirstMakesStaleActorLose()
    {
        return ExecuteRoleChangeLifecycleRaceAsync(resend: true);
    }

    [Fact]
    public async Task OrganizationDeactivateCreate_CommitFirstMakesMutationLose()
    {
        TestGraph graph = await SeedGraphAsync(OrganizationRole.Owner);
        using var timeout = CreateTimeout();
        await using EnmaDbContext blockerContext = fixture.CreateDbContext();
        await using IDbContextTransaction blocker =
            await blockerContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                timeout.Token);
        Organization organization = await blockerContext.Organizations
            .FromSqlInterpolated(
                $"""
                SELECT * FROM organizations
                WHERE id = {graph.Organization.Id}
                FOR UPDATE
                """)
            .SingleAsync(timeout.Token);
        organization.Deactivate();
        await blockerContext.SaveChangesAsync(timeout.Token);
        var entered = new SignalBeforeOrganizationLockInterceptor();
        Task<CreateOrganizationInvitationPersistenceResult> create =
            CreatePersistence(entered).CreateAsync(
                CreateRequest(
                    graph,
                    "inactive-organization@example.test",
                    OrganizationRole.Member),
                timeout.Token);
        await entered.CommandEntered.WaitAsync(timeout.Token);

        await blocker.CommitAsync(timeout.Token);
        CreateOrganizationInvitationPersistenceResult result =
            await create.WaitAsync(timeout.Token);

        Assert.Equal(
            CreateOrganizationInvitationPersistenceStatus.AccessDenied,
            result.Status);
        await using EnmaDbContext verification = fixture.CreateDbContext();
        Assert.Equal(0, await verification.OrganizationInvitations.CountAsync());
        Assert.Equal(0, await verification.AuditLogs.CountAsync());
    }

    [Fact]
    public async Task Create_LegalTaskCreationMembershipFirst_RetriesWithoutDeadlock()
    {
        TestGraph graph = await SeedGraphAsync(OrganizationRole.Owner);
        InactiveMember invitee = await SeedInactiveMemberAsync(graph.Organization);
        var operationalGate = new PauseAfterMembershipLockInterceptor();
        var invitationProbe = new InvitationRetryProbeInterceptor();
        var taskPersistence = new LegalTaskCreationPersistence(
            CreateOptions(operationalGate),
            new FixedTimeProvider(Now));
        using var timeout = CreateTimeout();
        Task<LegalTaskCreationPersistenceResult> taskCreation =
            taskPersistence.ExecuteAsync(
                new LegalTaskCreationPersistenceRequest(
                    graph.Actor.Id,
                    graph.Organization.Id,
                    graph.Membership.Id,
                    invitee.Membership.Id,
                    null),
                state =>
                {
                    Assert.False(state.Assignee?.IsMembershipActive);
                    return LegalTaskCreationDecision.AccessDenied;
                },
                timeout.Token);
        await operationalGate.MembershipLocked.WaitAsync(timeout.Token);
        Task<CreateOrganizationInvitationPersistenceResult> invitation =
            CreatePersistence(invitationProbe).CreateAsync(
                CreateRequest(graph, invitee.User.Email, OrganizationRole.Member),
                timeout.Token);

        try
        {
            await WaitForBlockedMembershipLockAsync(timeout.Token);
            Assert.False(taskCreation.IsCompleted);
            Assert.False(invitation.IsCompleted);
            operationalGate.Release();
            await Task.WhenAll(taskCreation, invitation).WaitAsync(timeout.Token);

            Assert.Equal(
                LegalTaskCreationDecisionStatus.AccessDenied,
                (await taskCreation).Status);
            Assert.Equal(
                CreateOrganizationInvitationPersistenceStatus.Succeeded,
                (await invitation).Status);
            Assert.Equal(1, invitationProbe.LockNotAvailableCount);
            Assert.Equal(2, invitationProbe.OrganizationLockCount);

            await using EnmaDbContext dbContext = fixture.CreateDbContext();
            Assert.Equal(0, await dbContext.LegalTasks.CountAsync());
            Assert.Equal(1, await dbContext.OrganizationInvitations.CountAsync());
            Assert.Equal(
                [AuditEventType.OrganizationInvitationCreated],
                await FindAuditTypesAsync(dbContext, timeout.Token));
        }
        finally
        {
            operationalGate.Release();
            await DrainAsync(taskCreation);
            await DrainAsync(invitation);
        }
    }

    [Fact]
    public async Task Resend_LegalTaskCreationMembershipFirst_RetriesWithoutDeadlock()
    {
        TestGraph graph = await SeedGraphAsync(OrganizationRole.Owner);
        OrganizationInvitation seededInvitation = await SeedInvitationAsync(
            graph,
            OrganizationRole.Administrator);
        var operationalGate = new PauseAfterMembershipLockInterceptor();
        var invitationProbe = new InvitationRetryProbeInterceptor();
        var taskPersistence = new LegalTaskCreationPersistence(
            CreateOptions(operationalGate),
            new FixedTimeProvider(Now));
        using var timeout = CreateTimeout();
        Task<LegalTaskCreationPersistenceResult> taskCreation =
            taskPersistence.ExecuteAsync(
                new LegalTaskCreationPersistenceRequest(
                    graph.Actor.Id,
                    graph.Organization.Id,
                    graph.Membership.Id,
                    null,
                    null),
                state => state.Actor?.IsMembershipActive == true &&
                    state.Actor.IsUserActive
                        ? LegalTaskCreationDecision.Persist(
                            new LegalTask(
                                graph.Organization.Id,
                                "Invitation cross-slice task",
                                null,
                                null,
                                null,
                                null,
                                graph.Membership.Id,
                                Now))
                        : LegalTaskCreationDecision.AccessDenied,
                timeout.Token);
        await operationalGate.MembershipLocked.WaitAsync(timeout.Token);
        Task<ResendOrganizationInvitationPersistenceResult> resend =
            CreatePersistence(invitationProbe).ResendAsync(
                LifecycleRequest(graph, seededInvitation.Id),
                timeout.Token);

        try
        {
            await WaitForBlockedMembershipLockAsync(timeout.Token);
            Assert.False(taskCreation.IsCompleted);
            Assert.False(resend.IsCompleted);
            operationalGate.Release();
            await Task.WhenAll(taskCreation, resend).WaitAsync(timeout.Token);

            Assert.Equal(
                LegalTaskCreationDecisionStatus.Persist,
                (await taskCreation).Status);
            ResendOrganizationInvitationPersistenceResult resendResult =
                await resend;
            Assert.Equal(
                ResendOrganizationInvitationPersistenceStatus.Succeeded,
                resendResult.Status);
            Assert.Equal(1, invitationProbe.LockNotAvailableCount);
            Assert.Equal(2, invitationProbe.OrganizationLockCount);
            var tokenService = new CryptographicOrganizationInvitationTokenService();
            Assert.True(tokenService.TryHashToken(
                resendResult.DeliveryRequest!.RawToken,
                out OrganizationInvitationTokenHash? rotatedHash));

            await using EnmaDbContext dbContext = fixture.CreateDbContext();
            OrganizationInvitation stored = await dbContext
                .OrganizationInvitations
                .SingleAsync();
            Assert.Equal(rotatedHash, stored.TokenHash);
            Assert.Equal(1, await dbContext.LegalTasks.CountAsync());
            Assert.Equal(
                [
                    AuditEventType.LegalTaskCreated,
                    AuditEventType.OrganizationInvitationResent
                ],
                await FindAuditTypesAsync(dbContext, timeout.Token));
        }
        finally
        {
            operationalGate.Release();
            await DrainAsync(taskCreation);
            await DrainAsync(resend);
        }
    }

    [Fact]
    public async Task Create_LegacyClientMutationMembershipFirst_RetriesWithoutDeadlock()
    {
        TestGraph graph = await SeedGraphAsync(OrganizationRole.Owner);
        var client = new Client(graph.Organization.Id, "Original client", Now);
        await SeedAsync(client);
        var operationalGate = new PauseAfterMembershipLockInterceptor();
        var invitationProbe = new InvitationRetryProbeInterceptor();
        var clientPersistence = new ClientMutationPersistence(
            CreateOptions(operationalGate),
            new FixedTimeProvider(Now));
        using var timeout = CreateTimeout();
        Task<ClientMutationPersistenceResult> clientMutation =
            clientPersistence.UpdateNameAsync(
                new ClientMutationPersistenceRequest(
                    graph.Actor.Id,
                    graph.Organization.Id,
                    graph.Membership.Id,
                    client.Id),
                state =>
                {
                    state.Client.ChangeName("Renamed client");
                    return ClientMutationDecision.Persist;
                },
                timeout.Token);
        await operationalGate.MembershipLocked.WaitAsync(timeout.Token);
        Task<CreateOrganizationInvitationPersistenceResult> invitation =
            CreatePersistence(invitationProbe).CreateAsync(
                CreateRequest(
                    graph,
                    "legacy-cross-slice@example.test",
                    OrganizationRole.Member),
                timeout.Token);

        try
        {
            await WaitForBlockedMembershipLockAsync(timeout.Token);
            Assert.False(clientMutation.IsCompleted);
            Assert.False(invitation.IsCompleted);
            operationalGate.Release();
            await Task.WhenAll(clientMutation, invitation).WaitAsync(timeout.Token);

            Assert.Equal(ClientMutationPersistenceResult.Succeeded, await clientMutation);
            Assert.Equal(
                CreateOrganizationInvitationPersistenceStatus.Succeeded,
                (await invitation).Status);
            Assert.Equal(1, invitationProbe.LockNotAvailableCount);
            Assert.Equal(2, invitationProbe.OrganizationLockCount);

            await using EnmaDbContext dbContext = fixture.CreateDbContext();
            Assert.Equal(
                "Renamed client",
                (await dbContext.Clients.SingleAsync()).Name);
            Assert.Equal(1, await dbContext.OrganizationInvitations.CountAsync());
            Assert.Equal(
                [
                    AuditEventType.ClientRenamed,
                    AuditEventType.OrganizationInvitationCreated
                ],
                await FindAuditTypesAsync(dbContext, timeout.Token));
        }
        finally
        {
            operationalGate.Release();
            await DrainAsync(clientMutation);
            await DrainAsync(invitation);
        }
    }

    [Fact]
    public async Task AcceptAccept_SameToken_AllowsOneEffectiveAcceptance()
    {
        TestGraph graph = await SeedGraphAsync(OrganizationRole.Owner);
        RecipientInvitation recipient = await SeedRecipientInvitationAsync(graph);
        var pause = new PauseAfterOrganizationLockInterceptor();
        using var timeout = CreateTimeout();

        Task<AcceptOrganizationInvitationPersistenceResult> first =
            CreatePersistence(pause).AcceptAsync(
                recipient.User.Id,
                recipient.TokenHash,
                timeout.Token);
        await pause.LockAcquired.WaitAsync(timeout.Token);
        Task<AcceptOrganizationInvitationPersistenceResult> second =
            CreatePersistence().AcceptAsync(
                recipient.User.Id,
                recipient.TokenHash,
                timeout.Token);

        pause.Release();
        AcceptOrganizationInvitationPersistenceResult[] results =
            await Task.WhenAll(first, second).WaitAsync(timeout.Token);

        Assert.Single(
            results,
            result => result ==
                AcceptOrganizationInvitationPersistenceResult.Succeeded);
        Assert.Single(
            results,
            result => result ==
                AcceptOrganizationInvitationPersistenceResult.Rejected);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Equal(1, await dbContext.OrganizationMemberships.CountAsync(
            membership => membership.OrganizationId == graph.Organization.Id &&
                membership.UserId == recipient.User.Id));
        Assert.Equal(1, await dbContext.AuditLogs.CountAsync(audit =>
            audit.EventType == AuditEventType.OrganizationInvitationAccepted));
        OrganizationInvitation stored = await dbContext.OrganizationInvitations
            .SingleAsync(invitation => invitation.Id == recipient.Invitation.Id);
        Assert.Null(stored.TokenHash);
        Assert.Equal(recipient.User.Id, stored.AcceptedByUserId);
    }

    [Fact]
    public async Task AcceptRevoke_AcceptanceFirstMakesRevokeConflict()
    {
        TestGraph graph = await SeedGraphAsync(OrganizationRole.Owner);
        RecipientInvitation recipient = await SeedRecipientInvitationAsync(graph);
        var pause = new PauseAfterOrganizationLockInterceptor();
        using var timeout = CreateTimeout();

        Task<AcceptOrganizationInvitationPersistenceResult> accept =
            CreatePersistence(pause).AcceptAsync(
                recipient.User.Id,
                recipient.TokenHash,
                timeout.Token);
        await pause.LockAcquired.WaitAsync(timeout.Token);
        Task<RevokeOrganizationInvitationPersistenceResult> revoke =
            CreatePersistence().RevokeAsync(
                LifecycleRequest(graph, recipient.Invitation.Id),
                timeout.Token);

        pause.Release();
        await Task.WhenAll(accept, revoke).WaitAsync(timeout.Token);

        Assert.Equal(
            AcceptOrganizationInvitationPersistenceResult.Succeeded,
            await accept);
        Assert.Equal(
            RevokeOrganizationInvitationPersistenceResult.Conflict,
            await revoke);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Equal(
            [AuditEventType.OrganizationInvitationAccepted],
            await FindAuditTypesAsync(dbContext, timeout.Token));
    }

    [Fact]
    public async Task AcceptResend_ResendFirstInvalidatesOldToken()
    {
        TestGraph graph = await SeedGraphAsync(OrganizationRole.Owner);
        RecipientInvitation recipient = await SeedRecipientInvitationAsync(graph);
        var pause = new PauseAfterOrganizationLockInterceptor();
        using var timeout = CreateTimeout();

        Task<ResendOrganizationInvitationPersistenceResult> resend =
            CreatePersistence(pause).ResendAsync(
                LifecycleRequest(graph, recipient.Invitation.Id),
                timeout.Token);
        await pause.LockAcquired.WaitAsync(timeout.Token);
        Task<AcceptOrganizationInvitationPersistenceResult> accept =
            CreatePersistence().AcceptAsync(
                recipient.User.Id,
                recipient.TokenHash,
                timeout.Token);

        pause.Release();
        await Task.WhenAll(resend, accept).WaitAsync(timeout.Token);

        ResendOrganizationInvitationPersistenceResult resendResult = await resend;
        Assert.Equal(
            ResendOrganizationInvitationPersistenceStatus.Succeeded,
            resendResult.Status);
        Assert.Equal(
            AcceptOrganizationInvitationPersistenceResult.Rejected,
            await accept);
        var tokenService = new CryptographicOrganizationInvitationTokenService();
        Assert.True(tokenService.TryHashToken(
            resendResult.DeliveryRequest!.RawToken,
            out OrganizationInvitationTokenHash? rotatedHash));
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        OrganizationInvitation stored = await dbContext.OrganizationInvitations
            .SingleAsync(invitation => invitation.Id == recipient.Invitation.Id);
        Assert.Equal(rotatedHash, stored.TokenHash);
        Assert.NotEqual(recipient.TokenHash, stored.TokenHash);
        Assert.Equal(0, await dbContext.OrganizationMemberships.CountAsync(
            membership => membership.OrganizationId == graph.Organization.Id &&
                membership.UserId == recipient.User.Id));
        Assert.Equal(
            [AuditEventType.OrganizationInvitationResent],
            await FindAuditTypesAsync(dbContext, timeout.Token));
    }

    [Fact]
    public async Task Accept_ExpirationBoundaryWhileWaiting_RejectsStaleToken()
    {
        TestGraph graph = await SeedGraphAsync(OrganizationRole.Owner);
        DateTimeOffset expiresAt = Now.AddMinutes(1);
        RecipientInvitation recipient = await SeedRecipientInvitationAsync(
            graph,
            expiresAt: expiresAt);
        var clock = new AdjustableTimeProvider(Now);
        var entered = new SignalBeforeOrganizationLockInterceptor();
        using var timeout = CreateTimeout();
        await using EnmaDbContext blockerContext = fixture.CreateDbContext();
        await using IDbContextTransaction blocker =
            await blockerContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                timeout.Token);
        await blockerContext.Organizations
            .FromSqlInterpolated(
                $"""
                SELECT * FROM organizations
                WHERE id = {graph.Organization.Id}
                FOR UPDATE
                """)
            .SingleAsync(timeout.Token);

        Task<AcceptOrganizationInvitationPersistenceResult> accept =
            CreatePersistence(entered, clock).AcceptAsync(
                recipient.User.Id,
                recipient.TokenHash,
                timeout.Token);
        await entered.CommandEntered.WaitAsync(timeout.Token);
        clock.UtcNow = expiresAt;
        await blocker.CommitAsync(timeout.Token);

        Assert.Equal(
            AcceptOrganizationInvitationPersistenceResult.Rejected,
            await accept.WaitAsync(timeout.Token));
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        OrganizationInvitation stored = await dbContext.OrganizationInvitations
            .SingleAsync(invitation => invitation.Id == recipient.Invitation.Id);
        Assert.Null(stored.AcceptedAt);
        Assert.Null(stored.ExpiredAt);
        Assert.Equal(recipient.TokenHash, stored.TokenHash);
        Assert.Equal(0, await dbContext.AuditLogs.CountAsync());
    }

    [Fact]
    public async Task Accept_ExpirationBoundaryWhileWaitingForUserLock_Rejects()
    {
        TestGraph graph = await SeedGraphAsync(OrganizationRole.Owner);
        DateTimeOffset expiresAt = Now.AddMinutes(1);
        RecipientInvitation recipient = await SeedRecipientInvitationAsync(
            graph,
            expiresAt: expiresAt);
        var clock = new AdjustableTimeProvider(Now);
        using var timeout = CreateTimeout();
        await using EnmaDbContext blockerContext = fixture.CreateDbContext();
        await using IDbContextTransaction blocker =
            await blockerContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                timeout.Token);
        await blockerContext.Users
            .FromSqlInterpolated(
                $"""
                SELECT * FROM users
                WHERE id = {recipient.User.Id}
                FOR UPDATE
                """)
            .SingleAsync(timeout.Token);
        Task<AcceptOrganizationInvitationPersistenceResult> accept =
            CreatePersistence(timeProvider: clock).AcceptAsync(
                recipient.User.Id,
                recipient.TokenHash,
                timeout.Token);
        bool blockerCompleted = false;

        try
        {
            await WaitForBlockedUserLockAsync(timeout.Token);
            clock.UtcNow = expiresAt;
            await blocker.CommitAsync(timeout.Token);
            blockerCompleted = true;

            Assert.Equal(
                AcceptOrganizationInvitationPersistenceResult.Rejected,
                await accept.WaitAsync(timeout.Token));
            await using EnmaDbContext dbContext = fixture.CreateDbContext();
            Assert.Equal(0, await dbContext.OrganizationMemberships.CountAsync(
                membership =>
                    membership.OrganizationId == graph.Organization.Id &&
                    membership.UserId == recipient.User.Id));
            Assert.Equal(0, await dbContext.AuditLogs.CountAsync());
            OrganizationInvitation stored = await dbContext
                .OrganizationInvitations
                .SingleAsync(invitation =>
                    invitation.Id == recipient.Invitation.Id);
            Assert.Null(stored.AcceptedAt);
            Assert.Null(stored.AcceptedByUserId);
            Assert.Equal(recipient.TokenHash, stored.TokenHash);
        }
        finally
        {
            if (!blockerCompleted)
            {
                await blocker.RollbackAsync(CancellationToken.None);
            }

            await DrainAsync(accept);
        }
    }

    [Fact]
    public async Task AcceptRoleChange_RoleChangeFirstMakesStaleInvitationLose()
    {
        TestGraph graph = await SeedGraphAsync(OrganizationRole.Owner);
        RecipientInvitation recipient = await SeedRecipientInvitationAsync(
            graph,
            includeMembership: true);
        var pause = new PauseAfterOrganizationLockInterceptor();
        var rolePersistence = new OrganizationMemberRoleMutationPersistence(
            CreateOptions(pause),
            new FixedTimeProvider(Now));
        var request = new OrganizationMemberRoleMutationPersistenceRequest(
            graph.Actor.Id,
            graph.Organization.Id,
            graph.Membership.Id,
            recipient.Membership!.Id,
            OrganizationRole.Administrator,
            OrganizationRole.Member);
        using var timeout = CreateTimeout();

        Task<OrganizationMemberRoleMutationPersistenceResult> roleChange =
            rolePersistence.ExecuteAsync(request, timeout.Token);
        await pause.LockAcquired.WaitAsync(timeout.Token);
        Task<AcceptOrganizationInvitationPersistenceResult> accept =
            CreatePersistence().AcceptAsync(
                recipient.User.Id,
                recipient.TokenHash,
                timeout.Token);

        pause.Release();
        await Task.WhenAll(roleChange, accept).WaitAsync(timeout.Token);

        Assert.Equal(
            OrganizationMemberRoleMutationPersistenceResult.Succeeded,
            await roleChange);
        Assert.Equal(
            AcceptOrganizationInvitationPersistenceResult.Rejected,
            await accept);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Equal(
            OrganizationRole.Administrator,
            (await dbContext.OrganizationMemberships.SingleAsync(membership =>
                membership.Id == recipient.Membership.Id)).Role);
        Assert.Equal(
            [AuditEventType.OrganizationMembershipRoleChanged],
            await FindAuditTypesAsync(dbContext, timeout.Token));
        Assert.Equal(
            recipient.TokenHash,
            (await dbContext.OrganizationInvitations.SingleAsync(invitation =>
                invitation.Id == recipient.Invitation.Id)).TokenHash);
    }

    [Fact]
    public async Task AcceptReactivation_ReactivationFirstReusesMembershipWithoutDuplicate()
    {
        TestGraph graph = await SeedGraphAsync(OrganizationRole.Owner);
        RecipientInvitation recipient = await SeedRecipientInvitationAsync(
            graph,
            includeMembership: true,
            membershipActive: false);
        var pause = new PauseAfterOrganizationLockInterceptor();
        var lifecyclePersistence =
            new OrganizationMemberLifecycleMutationPersistence(
                CreateOptions(pause),
                new FixedTimeProvider(Now));
        var request = new OrganizationMemberLifecycleMutationPersistenceRequest(
            graph.Actor.Id,
            graph.Organization.Id,
            graph.Membership.Id,
            recipient.Membership!.Id,
            OrganizationMemberLifecycleOperation.Reactivate);
        using var timeout = CreateTimeout();

        Task<OrganizationMemberLifecycleMutationPersistenceResult> reactivate =
            lifecyclePersistence.ExecuteAsync(request, timeout.Token);
        await pause.LockAcquired.WaitAsync(timeout.Token);
        Task<AcceptOrganizationInvitationPersistenceResult> accept =
            CreatePersistence().AcceptAsync(
                recipient.User.Id,
                recipient.TokenHash,
                timeout.Token);

        pause.Release();
        await Task.WhenAll(reactivate, accept).WaitAsync(timeout.Token);

        Assert.Equal(
            OrganizationMemberLifecycleMutationPersistenceResult.Succeeded,
            await reactivate);
        Assert.Equal(
            AcceptOrganizationInvitationPersistenceResult.Succeeded,
            await accept);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        OrganizationMembership stored = await dbContext.OrganizationMemberships
            .SingleAsync(membership =>
                membership.OrganizationId == graph.Organization.Id &&
                membership.UserId == recipient.User.Id);
        Assert.Equal(recipient.Membership.Id, stored.Id);
        Assert.True(stored.IsActive);
        Assert.Equal(
            [
                AuditEventType.OrganizationMembershipReactivated,
                AuditEventType.OrganizationInvitationAccepted
            ],
            await FindAuditTypesAsync(dbContext, timeout.Token));
    }

    [Fact]
    public async Task AcceptDeactivation_DeactivationFirstReactivatesCompatibleMembership()
    {
        TestGraph graph = await SeedGraphAsync(OrganizationRole.Owner);
        RecipientInvitation recipient = await SeedRecipientInvitationAsync(
            graph,
            includeMembership: true);
        var pause = new PauseAfterOrganizationLockInterceptor();
        var lifecyclePersistence =
            new OrganizationMemberLifecycleMutationPersistence(
                CreateOptions(pause),
                new FixedTimeProvider(Now));
        var request = new OrganizationMemberLifecycleMutationPersistenceRequest(
            graph.Actor.Id,
            graph.Organization.Id,
            graph.Membership.Id,
            recipient.Membership!.Id,
            OrganizationMemberLifecycleOperation.Deactivate);
        using var timeout = CreateTimeout();

        Task<OrganizationMemberLifecycleMutationPersistenceResult> deactivate =
            lifecyclePersistence.ExecuteAsync(request, timeout.Token);
        await pause.LockAcquired.WaitAsync(timeout.Token);
        Task<AcceptOrganizationInvitationPersistenceResult> accept =
            CreatePersistence().AcceptAsync(
                recipient.User.Id,
                recipient.TokenHash,
                timeout.Token);

        pause.Release();
        await Task.WhenAll(deactivate, accept).WaitAsync(timeout.Token);

        Assert.Equal(
            OrganizationMemberLifecycleMutationPersistenceResult.Succeeded,
            await deactivate);
        Assert.Equal(
            AcceptOrganizationInvitationPersistenceResult.Succeeded,
            await accept);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        OrganizationMembership stored = await dbContext.OrganizationMemberships
            .SingleAsync(membership =>
                membership.OrganizationId == graph.Organization.Id &&
                membership.UserId == recipient.User.Id);
        Assert.Equal(recipient.Membership.Id, stored.Id);
        Assert.True(stored.IsActive);
        Assert.Equal(
            [
                AuditEventType.OrganizationMembershipDeactivated,
                AuditEventType.OrganizationInvitationAccepted
            ],
            await FindAuditTypesAsync(dbContext, timeout.Token));
    }

    [Fact]
    public async Task AcceptOrganizationDeactivate_DeactivationFirstRejectsStaleState()
    {
        TestGraph graph = await SeedGraphAsync(OrganizationRole.Owner);
        RecipientInvitation recipient = await SeedRecipientInvitationAsync(graph);
        var entered = new SignalBeforeOrganizationLockInterceptor();
        using var timeout = CreateTimeout();
        await using EnmaDbContext blockerContext = fixture.CreateDbContext();
        await using IDbContextTransaction blocker =
            await blockerContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                timeout.Token);
        Organization organization = await blockerContext.Organizations
            .FromSqlInterpolated(
                $"""
                SELECT * FROM organizations
                WHERE id = {graph.Organization.Id}
                FOR UPDATE
                """)
            .SingleAsync(timeout.Token);
        organization.Deactivate();
        await blockerContext.SaveChangesAsync(timeout.Token);

        Task<AcceptOrganizationInvitationPersistenceResult> accept =
            CreatePersistence(entered).AcceptAsync(
                recipient.User.Id,
                recipient.TokenHash,
                timeout.Token);
        await entered.CommandEntered.WaitAsync(timeout.Token);
        await blocker.CommitAsync(timeout.Token);

        Assert.Equal(
            AcceptOrganizationInvitationPersistenceResult.Rejected,
            await accept.WaitAsync(timeout.Token));
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Equal(0, await dbContext.OrganizationMemberships.CountAsync(
            membership => membership.OrganizationId == graph.Organization.Id &&
                membership.UserId == recipient.User.Id));
        Assert.Equal(0, await dbContext.AuditLogs.CountAsync());
        Assert.Equal(
            recipient.TokenHash,
            (await dbContext.OrganizationInvitations.SingleAsync(invitation =>
                invitation.Id == recipient.Invitation.Id)).TokenHash);
    }

    [Fact]
    public async Task Accept_LegacyMembershipFirst_RetriesWithoutDeadlock()
    {
        TestGraph graph = await SeedGraphAsync(OrganizationRole.Owner);
        RecipientInvitation recipient = await SeedRecipientInvitationAsync(
            graph,
            includeMembership: true,
            membershipActive: false);
        var operationalGate = new PauseAfterMembershipLockInterceptor();
        var invitationProbe = new InvitationRetryProbeInterceptor();
        var taskPersistence = new LegalTaskCreationPersistence(
            CreateOptions(operationalGate),
            new FixedTimeProvider(Now));
        using var timeout = CreateTimeout();
        Task<LegalTaskCreationPersistenceResult> taskCreation =
            taskPersistence.ExecuteAsync(
                new LegalTaskCreationPersistenceRequest(
                    graph.Actor.Id,
                    graph.Organization.Id,
                    graph.Membership.Id,
                    recipient.Membership!.Id,
                    null),
                state =>
                {
                    Assert.False(state.Assignee?.IsMembershipActive);
                    return LegalTaskCreationDecision.AccessDenied;
                },
                timeout.Token);
        await operationalGate.MembershipLocked.WaitAsync(timeout.Token);
        Task<AcceptOrganizationInvitationPersistenceResult> accept =
            CreatePersistence(invitationProbe).AcceptAsync(
                recipient.User.Id,
                recipient.TokenHash,
                timeout.Token);

        try
        {
            await WaitForBlockedMembershipLockAsync(timeout.Token);
            Assert.False(taskCreation.IsCompleted);
            Assert.False(accept.IsCompleted);
            operationalGate.Release();
            await Task.WhenAll(taskCreation, accept).WaitAsync(timeout.Token);

            Assert.Equal(
                LegalTaskCreationDecisionStatus.AccessDenied,
                (await taskCreation).Status);
            Assert.Equal(
                AcceptOrganizationInvitationPersistenceResult.Succeeded,
                await accept);
            Assert.Equal(1, invitationProbe.LockNotAvailableCount);
            Assert.Equal(2, invitationProbe.OrganizationLockCount);
            await using EnmaDbContext dbContext = fixture.CreateDbContext();
            OrganizationMembership stored = await dbContext
                .OrganizationMemberships
                .SingleAsync(membership =>
                    membership.OrganizationId == graph.Organization.Id &&
                    membership.UserId == recipient.User.Id);
            Assert.Equal(recipient.Membership.Id, stored.Id);
            Assert.True(stored.IsActive);
            Assert.Equal(0, await dbContext.LegalTasks.CountAsync());
            Assert.Equal(
                [AuditEventType.OrganizationInvitationAccepted],
                await FindAuditTypesAsync(dbContext, timeout.Token));
        }
        finally
        {
            operationalGate.Release();
            await DrainAsync(taskCreation);
            await DrainAsync(accept);
        }
    }

    private async Task ExecuteRoleChangeLifecycleRaceAsync(bool resend)
    {
        AdministrationGraph graph = await SeedAdministrationGraphAsync();
        var actorGraph = new TestGraph(
            graph.Organization,
            graph.Administrator,
            graph.AdministratorMembership);
        OrganizationInvitation invitation = await SeedInvitationAsync(
            actorGraph,
            OrganizationRole.Member);
        var pause = new PauseAfterOrganizationLockInterceptor();
        var rolePersistence = new OrganizationMemberRoleMutationPersistence(
            CreateOptions(pause),
            new FixedTimeProvider(Now));
        var roleRequest = new OrganizationMemberRoleMutationPersistenceRequest(
            graph.Owner.Id,
            graph.Organization.Id,
            graph.OwnerMembership.Id,
            graph.AdministratorMembership.Id,
            OrganizationRole.Member,
            OrganizationRole.Administrator);
        OrganizationInvitationMutationPersistenceRequest lifecycleRequest =
            LifecycleRequest(actorGraph, invitation.Id);
        using var timeout = CreateTimeout();

        Task<OrganizationMemberRoleMutationPersistenceResult> roleChange =
            rolePersistence.ExecuteAsync(roleRequest, timeout.Token);
        await pause.LockAcquired.WaitAsync(timeout.Token);

        if (resend)
        {
            Task<ResendOrganizationInvitationPersistenceResult> lifecycle =
                CreatePersistence().ResendAsync(
                    lifecycleRequest,
                    timeout.Token);
            pause.Release();
            await Task.WhenAll(roleChange, lifecycle).WaitAsync(timeout.Token);
            Assert.Equal(
                ResendOrganizationInvitationPersistenceStatus.AccessDenied,
                (await lifecycle).Status);
        }
        else
        {
            Task<RevokeOrganizationInvitationPersistenceResult> lifecycle =
                CreatePersistence().RevokeAsync(
                    lifecycleRequest,
                    timeout.Token);
            pause.Release();
            await Task.WhenAll(roleChange, lifecycle).WaitAsync(timeout.Token);
            Assert.Equal(
                RevokeOrganizationInvitationPersistenceResult.AccessDenied,
                await lifecycle);
        }

        Assert.Equal(
            OrganizationMemberRoleMutationPersistenceResult.Succeeded,
            await roleChange);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        OrganizationInvitation stored = await dbContext
            .OrganizationInvitations
            .SingleAsync();
        Assert.Equal(OrganizationInvitationState.Pending, stored.GetState(Now));
        Assert.Equal(
            [AuditEventType.OrganizationMembershipRoleChanged],
            await dbContext.AuditLogs
                .Select(audit => audit.EventType)
                .ToArrayAsync());
    }

    private OrganizationInvitationMutationPersistence CreatePersistence(
        IInterceptor? interceptor = null,
        TimeProvider? timeProvider = null)
    {
        return new OrganizationInvitationMutationPersistence(
            CreateOptions(interceptor),
            timeProvider ?? new FixedTimeProvider(Now),
            new CryptographicOrganizationInvitationTokenService());
    }

    private DbContextOptions<EnmaDbContext> CreateOptions(
        IInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<EnmaDbContext>()
            .UseNpgsql(fixture.ConnectionString);

        if (interceptor is not null)
        {
            builder.AddInterceptors(interceptor);
        }

        return builder.Options;
    }

    private static CreateOrganizationInvitationPersistenceRequest CreateRequest(
        TestGraph graph,
        string email,
        OrganizationRole role)
    {
        return new CreateOrganizationInvitationPersistenceRequest(
            graph.Actor.Id,
            graph.Organization.Id,
            graph.Membership.Id,
            email,
            role);
    }

    private static OrganizationInvitationMutationPersistenceRequest
        LifecycleRequest(TestGraph graph, Guid invitationId)
    {
        return new OrganizationInvitationMutationPersistenceRequest(
            graph.Actor.Id,
            graph.Organization.Id,
            graph.Membership.Id,
            invitationId);
    }

    private async Task<TestGraph> SeedGraphAsync(OrganizationRole role)
    {
        string suffix = Guid.NewGuid().ToString("N");
        var organization = new Organization(
            "Concurrency Invitations",
            $"invitation-race-{suffix}",
            CreatedAt);
        var actor = new User(
            "Invitation Actor",
            $"invitation-actor-{suffix}@example.test",
            CreatedAt);
        var membership = new OrganizationMembership(
            organization.Id,
            actor.Id,
            role,
            CreatedAt);
        await SeedAsync(organization, actor, membership);
        return new TestGraph(organization, actor, membership);
    }

    private async Task<AdministrationGraph> SeedAdministrationGraphAsync()
    {
        string suffix = Guid.NewGuid().ToString("N");
        var organization = new Organization(
            "Administration Race",
            $"administration-race-{suffix}",
            CreatedAt);
        var owner = new User(
            "Owner",
            $"owner-{suffix}@example.test",
            CreatedAt);
        var administrator = new User(
            "Administrator",
            $"administrator-{suffix}@example.test",
            CreatedAt);
        var ownerMembership = new OrganizationMembership(
            organization.Id,
            owner.Id,
            OrganizationRole.Owner,
            CreatedAt);
        var administratorMembership = new OrganizationMembership(
            organization.Id,
            administrator.Id,
            OrganizationRole.Administrator,
            CreatedAt);
        await SeedAsync(
            organization,
            owner,
            administrator,
            ownerMembership,
            administratorMembership);
        return new AdministrationGraph(
            organization,
            owner,
            ownerMembership,
            administrator,
            administratorMembership);
    }

    private async Task<InactiveMember> SeedInactiveMemberAsync(
        Organization organization)
    {
        string suffix = Guid.NewGuid().ToString("N");
        var user = new User(
            "Inactive Invitee",
            $"inactive-invitee-{suffix}@example.test",
            CreatedAt);
        var membership = new OrganizationMembership(
            organization.Id,
            user.Id,
            OrganizationRole.Member,
            CreatedAt);
        membership.Deactivate();
        await SeedAsync(user, membership);
        return new InactiveMember(user, membership);
    }

    private async Task<OrganizationInvitation> SeedInvitationAsync(
        TestGraph graph,
        OrganizationRole role,
        OrganizationInvitationTokenHash? tokenHash = null)
    {
        var invitation = new OrganizationInvitation(
            graph.Organization.Id,
            $"race-{Guid.NewGuid():N}@example.test",
            role,
            graph.Membership.Id,
            tokenHash ?? RandomHash(),
            CreatedAt,
            Now.AddMinutes(-2),
            Now.AddDays(7));
        await SeedAsync(invitation);
        return invitation;
    }

    private async Task<RecipientInvitation> SeedRecipientInvitationAsync(
        TestGraph graph,
        OrganizationRole role = OrganizationRole.Member,
        bool includeMembership = false,
        bool membershipActive = true,
        DateTimeOffset? expiresAt = null)
    {
        string suffix = Guid.NewGuid().ToString("N");
        var user = new User(
            "Invitation Recipient",
            $"recipient-{suffix}@example.test",
            CreatedAt);
        user.VerifyEmail(CreatedAt.AddMinutes(1));
        OrganizationMembership? membership = includeMembership
            ? new OrganizationMembership(
                graph.Organization.Id,
                user.Id,
                role,
                CreatedAt)
            : null;

        if (membership is not null && !membershipActive)
        {
            membership.Deactivate();
        }

        var tokenService = new CryptographicOrganizationInvitationTokenService();
        tokenService.GenerateToken(out var tokenHash);
        var invitation = new OrganizationInvitation(
            graph.Organization.Id,
            user.Email,
            role,
            graph.Membership.Id,
            tokenHash,
            CreatedAt,
            Now.AddMinutes(-2),
            expiresAt ?? Now.AddDays(7));

        if (membership is null)
        {
            await SeedAsync(user, invitation);
        }
        else
        {
            await SeedAsync(user, membership, invitation);
        }

        return new RecipientInvitation(user, invitation, tokenHash, membership);
    }

    private async Task SeedAsync(params object[] entities)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.AddRange(entities);
        await dbContext.SaveChangesAsync();
    }

    private async Task AssertDatabaseStateAsync(
        int invitationCount,
        AuditEventType eventType,
        int expectedAuditCount)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Equal(
            invitationCount,
            await dbContext.OrganizationInvitations.CountAsync());
        Assert.Equal(
            expectedAuditCount,
            await dbContext.AuditLogs.CountAsync(audit =>
                audit.EventType == eventType));
    }

    private async Task WaitForBlockedMembershipLockAsync(
        CancellationToken cancellationToken)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();

        while (true)
        {
            int count = await dbContext.Database.SqlQuery<int>(
                $"""
                SELECT COUNT(*)::integer AS "Value"
                FROM pg_stat_activity
                WHERE datname = current_database()
                  AND pid <> pg_backend_pid()
                  AND wait_event_type = 'Lock'
                  AND query ILIKE '%FROM organization_memberships%'
                  AND query ILIKE '%FOR UPDATE%'
                  AND query NOT ILIKE '%NOWAIT%'
                """).SingleAsync(cancellationToken);

            if (count > 0)
            {
                return;
            }

            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private async Task WaitForBlockedUserLockAsync(
        CancellationToken cancellationToken)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();

        while (true)
        {
            int count = await dbContext.Database.SqlQuery<int>(
                $"""
                SELECT COUNT(*)::integer AS "Value"
                FROM pg_stat_activity
                WHERE datname = current_database()
                  AND pid <> pg_backend_pid()
                  AND wait_event_type = 'Lock'
                  AND query ILIKE '%FROM users%'
                  AND query ILIKE '%FOR UPDATE%'
                """).SingleAsync(cancellationToken);

            if (count > 0)
            {
                return;
            }

            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static Task<List<AuditEventType>> FindAuditTypesAsync(
        EnmaDbContext dbContext,
        CancellationToken cancellationToken)
    {
        return dbContext.AuditLogs
            .AsNoTracking()
            .Select(audit => audit.EventType)
            .OrderBy(eventType => eventType)
            .ToListAsync(cancellationToken);
    }

    private static async Task DrainAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception)
        {
        }
    }

    private static OrganizationInvitationTokenHash RandomHash()
    {
        return new OrganizationInvitationTokenHash(
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
    }

    private static CancellationTokenSource CreateTimeout()
    {
        return new CancellationTokenSource(TimeSpan.FromSeconds(30));
    }

    private sealed record TestGraph(
        Organization Organization,
        User Actor,
        OrganizationMembership Membership);

    private sealed record AdministrationGraph(
        Organization Organization,
        User Owner,
        OrganizationMembership OwnerMembership,
        User Administrator,
        OrganizationMembership AdministratorMembership);

    private sealed record InactiveMember(
        User User,
        OrganizationMembership Membership);

    private sealed record RecipientInvitation(
        User User,
        OrganizationInvitation Invitation,
        OrganizationInvitationTokenHash TokenHash,
        OrganizationMembership? Membership);

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class AdjustableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class PauseAfterOrganizationLockInterceptor
        : DbCommandInterceptor
    {
        private readonly TaskCompletionSource<bool> lockAcquired = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task LockAcquired => lockAcquired.Task;

        public void Release() => release.TrySetResult(true);

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (IsOrganizationLock(command))
            {
                lockAcquired.TrySetResult(true);
                await release.Task.WaitAsync(cancellationToken);
            }

            return result;
        }
    }

    private sealed class SignalBeforeOrganizationLockInterceptor
        : DbCommandInterceptor
    {
        private readonly TaskCompletionSource<bool> commandEntered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task CommandEntered => commandEntered.Task;

        public override ValueTask<InterceptionResult<DbDataReader>>
            ReaderExecutingAsync(
                DbCommand command,
                CommandEventData eventData,
                InterceptionResult<DbDataReader> result,
                CancellationToken cancellationToken = default)
        {
            if (IsOrganizationLock(command))
            {
                commandEntered.TrySetResult(true);
            }

            return ValueTask.FromResult(result);
        }
    }

    private sealed class PauseAfterMembershipLockInterceptor
        : DbCommandInterceptor
    {
        private readonly TaskCompletionSource<bool> membershipLocked = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int paused;

        public Task MembershipLocked => membershipLocked.Task;

        public void Release() => release.TrySetResult(true);

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains(
                    "FROM organization_memberships",
                    StringComparison.Ordinal) &&
                command.CommandText.Contains("FOR UPDATE", StringComparison.Ordinal) &&
                Interlocked.CompareExchange(ref paused, 1, 0) == 0)
            {
                membershipLocked.TrySetResult(true);
                await release.Task.WaitAsync(cancellationToken);
            }

            return result;
        }
    }

    private sealed class InvitationRetryProbeInterceptor : DbCommandInterceptor
    {
        private int lockNotAvailableCount;
        private int organizationLockCount;

        public int LockNotAvailableCount => lockNotAvailableCount;

        public int OrganizationLockCount => organizationLockCount;

        public override ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (IsOrganizationLock(command))
            {
                Interlocked.Increment(ref organizationLockCount);
            }

            return ValueTask.FromResult(result);
        }

        public override Task CommandFailedAsync(
            DbCommand command,
            CommandErrorEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains(
                    "FROM organization_memberships",
                    StringComparison.Ordinal) &&
                command.CommandText.Contains(
                    "FOR UPDATE NOWAIT",
                    StringComparison.Ordinal) &&
                HasSqlState(eventData.Exception, "55P03"))
            {
                Interlocked.Increment(ref lockNotAvailableCount);
            }

            return Task.CompletedTask;
        }

        private static bool HasSqlState(Exception exception, string sqlState)
        {
            for (Exception? current = exception;
                 current is not null;
                 current = current.InnerException)
            {
                if (current is PostgresException postgresException &&
                    postgresException.SqlState == sqlState)
                {
                    return true;
                }
            }

            return false;
        }
    }

    private static bool IsOrganizationLock(DbCommand command)
    {
        return command.CommandText.Contains(
                "FROM organizations",
                StringComparison.Ordinal) &&
            command.CommandText.Contains("FOR UPDATE", StringComparison.Ordinal);
    }
}
