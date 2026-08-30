using Enma.Application.Organizations.Invitations;
using Enma.Domain.Organizations;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Enma.IntegrationTests.Infrastructure.Persistence;

[Collection(PostgreSqlCollection.Name)]
public sealed class OrganizationInvitationPersistenceTests(
    PostgreSqlFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        8,
        30,
        12,
        0,
        0,
        TimeSpan.Zero);
    private static readonly DateTimeOffset TokenIssuedAt =
        CreatedAt.AddMinutes(1);
    private static readonly DateTimeOffset ExpiresAt =
        TokenIssuedAt.Add(OrganizationInvitationPolicy.TokenLifetime);

    public Task InitializeAsync() => fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task SaveChanges_WithPendingThenAcceptedInvitation_PersistsBothShapes()
    {
        OrganizationGraph graph = await SeedOrganizationAsync("persistence");
        User acceptedBy = await SeedUserAsync(
            "Invitation Recipient",
            "recipient.persistence@example.test");
        OrganizationInvitationTokenHash tokenHash = CreateTokenHash(1);
        OrganizationInvitation invitation = CreateInvitation(graph, tokenHash: tokenHash);

        await using (EnmaDbContext createContext = fixture.CreateDbContext())
        {
            createContext.OrganizationInvitations.Add(invitation);
            await createContext.SaveChangesAsync();
        }

        await using (EnmaDbContext acceptContext = fixture.CreateDbContext())
        {
            OrganizationInvitation tracked = await acceptContext
                .OrganizationInvitations
                .SingleAsync(candidate => candidate.Id == invitation.Id);
            Assert.Equal(tokenHash, tracked.TokenHash);
            Assert.Equal("invited@example.test", tracked.InvitedEmail);
            tracked.Accept(acceptedBy.Id, TokenIssuedAt.AddHours(1));
            await acceptContext.SaveChangesAsync();
        }

        await using EnmaDbContext verificationContext = fixture.CreateDbContext();
        OrganizationInvitation persisted = await verificationContext
            .OrganizationInvitations
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == invitation.Id);

        Assert.Equal(graph.Organization.Id, persisted.OrganizationId);
        Assert.Equal(graph.Membership.Id, persisted.CreatedByMembershipId);
        Assert.Equal(OrganizationRole.Member, persisted.Role);
        Assert.Equal(acceptedBy.Id, persisted.AcceptedByUserId);
        Assert.Equal(TokenIssuedAt.AddHours(1), persisted.AcceptedAt);
        Assert.Null(persisted.TokenHash);
        Assert.Equal(
            OrganizationInvitationState.Accepted,
            persisted.GetState(TokenIssuedAt.AddHours(1)));
    }

    [Fact]
    public async Task SaveChanges_WithCreatorMembershipFromAnotherOrganization_ViolatesTenantForeignKey()
    {
        OrganizationGraph first = await SeedOrganizationAsync("tenant-one");
        OrganizationGraph second = await SeedOrganizationAsync("tenant-two");
        var invitation = new OrganizationInvitation(
            second.Organization.Id,
            "cross-tenant@example.test",
            OrganizationRole.Member,
            first.Membership.Id,
            CreateTokenHash(2),
            CreatedAt,
            TokenIssuedAt,
            ExpiresAt);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.OrganizationInvitations.Add(invitation);

        DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => dbContext.SaveChangesAsync());

        AssertPostgresException(
            exception,
            PostgresErrorCodes.ForeignKeyViolation,
            "fk_organization_invitations_memberships_org_created_by_id");
    }

    [Fact]
    public async Task Database_WithOwnerRole_RejectsRow()
    {
        OrganizationGraph graph = await SeedOrganizationAsync("owner-role");
        InvitationRow row = ValidRow(graph) with
        {
            Role = OrganizationRole.Owner
        };

        await AssertInsertRejectedAsync(
            row,
            PostgresErrorCodes.CheckViolation,
            "ck_organization_invitations_role");
    }

    [Fact]
    public async Task Database_WithInvalidTokenHashLength_RejectsRow()
    {
        OrganizationGraph graph = await SeedOrganizationAsync("hash-length");
        InvitationRow row = ValidRow(graph) with
        {
            TokenHash = new byte[31]
        };

        await AssertInsertRejectedAsync(
            row,
            PostgresErrorCodes.CheckViolation,
            "ck_organization_invitations_token_hash_length");
    }

    [Fact]
    public async Task SaveChanges_WithDuplicateTokenHash_ViolatesUniqueIndex()
    {
        OrganizationGraph first = await SeedOrganizationAsync("hash-one");
        OrganizationGraph second = await SeedOrganizationAsync("hash-two");
        byte[] sharedHash = CreateHashBytes(4);

        await SaveInvitationAsync(new OrganizationInvitation(
            first.Organization.Id,
            "first.hash@example.test",
            OrganizationRole.Member,
            first.Membership.Id,
            new OrganizationInvitationTokenHash(sharedHash),
            CreatedAt,
            TokenIssuedAt,
            ExpiresAt));

        var duplicate = new OrganizationInvitation(
            second.Organization.Id,
            "second.hash@example.test",
            OrganizationRole.Member,
            second.Membership.Id,
            new OrganizationInvitationTokenHash((byte[])sharedHash.Clone()),
            CreatedAt,
            TokenIssuedAt,
            ExpiresAt);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.OrganizationInvitations.Add(duplicate);
        DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => dbContext.SaveChangesAsync());

        AssertPostgresException(
            exception,
            PostgresErrorCodes.UniqueViolation,
            "ux_organization_invitations_token_hash");
    }

    [Fact]
    public async Task SaveChanges_WithSecondOpenInvitationForSameOrganizationAndEmail_ViolatesUniqueIndex()
    {
        OrganizationGraph graph = await SeedOrganizationAsync("open-duplicate");
        await SaveInvitationAsync(CreateInvitation(graph, hashSeed: 5));

        OrganizationInvitation duplicate = CreateInvitation(graph, hashSeed: 6);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.OrganizationInvitations.Add(duplicate);

        DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => dbContext.SaveChangesAsync());

        AssertPostgresException(
            exception,
            PostgresErrorCodes.UniqueViolation,
            "ux_organization_invitations_open_organization_id_email");
    }

    [Fact]
    public async Task SaveChanges_WithSameEmailInDifferentOrganizations_Succeeds()
    {
        OrganizationGraph first = await SeedOrganizationAsync("same-email-one");
        OrganizationGraph second = await SeedOrganizationAsync("same-email-two");

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.OrganizationInvitations.AddRange(
            CreateInvitation(first, hashSeed: 7),
            CreateInvitation(second, hashSeed: 8));
        await dbContext.SaveChangesAsync();

        Assert.Equal(2, await dbContext.OrganizationInvitations.CountAsync());
    }

    [Theory]
    [InlineData(OrganizationInvitationState.Accepted)]
    [InlineData(OrganizationInvitationState.Revoked)]
    [InlineData(OrganizationInvitationState.Expired)]
    public async Task SaveChanges_AfterTerminalization_AllowsNewInvitationForEmail(
        OrganizationInvitationState terminalState)
    {
        OrganizationGraph graph = await SeedOrganizationAsync(
            $"terminal-{terminalState.ToString().ToLowerInvariant()}");
        User acceptedBy = await SeedUserAsync(
            "Terminal Recipient",
            $"terminal.{terminalState.ToString().ToLowerInvariant()}@example.test");
        OrganizationInvitation first = CreateInvitation(graph, hashSeed: 9);
        await SaveInvitationAsync(first);

        await using (EnmaDbContext terminalContext = fixture.CreateDbContext())
        {
            OrganizationInvitation tracked = await terminalContext
                .OrganizationInvitations
                .SingleAsync(candidate => candidate.Id == first.Id);
            MaterializeTerminalState(tracked, terminalState, acceptedBy.Id);
            await terminalContext.SaveChangesAsync();
        }

        var replacement = new OrganizationInvitation(
            graph.Organization.Id,
            first.InvitedEmail,
            OrganizationRole.Administrator,
            graph.Membership.Id,
            CreateTokenHash(10),
            ExpiresAt.AddMinutes(1),
            ExpiresAt.AddMinutes(1),
            ExpiresAt.AddMinutes(1).Add(
                OrganizationInvitationPolicy.TokenLifetime));
        await SaveInvitationAsync(replacement);

        await using EnmaDbContext verificationContext = fixture.CreateDbContext();
        Assert.Equal(
            2,
            await verificationContext.OrganizationInvitations.CountAsync());
    }

    [Fact]
    public async Task ExpiredButNotMaterialized_ContinuesOccupyingOpenUniqueIndexUntilExpiredAtIsSet()
    {
        OrganizationGraph graph = await SeedOrganizationAsync("lazy-expiration");
        OrganizationInvitation expired = CreateInvitation(graph, hashSeed: 11);
        await SaveInvitationAsync(expired);

        var blockedReplacement = new OrganizationInvitation(
            graph.Organization.Id,
            expired.InvitedEmail,
            OrganizationRole.Member,
            graph.Membership.Id,
            CreateTokenHash(12),
            ExpiresAt.AddMinutes(1),
            ExpiresAt.AddMinutes(1),
            ExpiresAt.AddMinutes(1).Add(
                OrganizationInvitationPolicy.TokenLifetime));

        await using (EnmaDbContext blockedContext = fixture.CreateDbContext())
        {
            blockedContext.OrganizationInvitations.Add(blockedReplacement);
            DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(
                () => blockedContext.SaveChangesAsync());
            AssertPostgresException(
                exception,
                PostgresErrorCodes.UniqueViolation,
                "ux_organization_invitations_open_organization_id_email");
        }

        await using (EnmaDbContext expirationContext = fixture.CreateDbContext())
        {
            OrganizationInvitation tracked = await expirationContext
                .OrganizationInvitations
                .SingleAsync(candidate => candidate.Id == expired.Id);
            Assert.Equal(
                OrganizationInvitationState.Expired,
                tracked.GetState(ExpiresAt));
            Assert.Null(tracked.ExpiredAt);
            tracked.Expire(ExpiresAt);
            await expirationContext.SaveChangesAsync();
        }

        await SaveInvitationAsync(blockedReplacement);
        await using EnmaDbContext verificationContext = fixture.CreateDbContext();
        Assert.Equal(
            2,
            await verificationContext.OrganizationInvitations.CountAsync());
    }

    [Theory]
    [InlineData("accepted-without-user")]
    [InlineData("user-without-accepted")]
    [InlineData("pending-without-token")]
    [InlineData("terminal-with-token")]
    [InlineData("multiple-terminal")]
    public async Task Database_WithInvalidStateShape_RejectsRow(string scenario)
    {
        OrganizationGraph graph = await SeedOrganizationAsync($"state-{scenario}");
        User acceptedBy = await SeedUserAsync(
            "State Recipient",
            $"state.{scenario}@example.test");
        DateTimeOffset terminalAt = TokenIssuedAt.AddHours(1);
        InvitationRow row = ValidRow(graph);
        row = scenario switch
        {
            "accepted-without-user" => row with
            {
                AcceptedAt = terminalAt,
                TokenHash = null
            },
            "user-without-accepted" => row with
            {
                AcceptedByUserId = acceptedBy.Id
            },
            "pending-without-token" => row with
            {
                TokenHash = null
            },
            "terminal-with-token" => row with
            {
                RevokedAt = terminalAt
            },
            "multiple-terminal" => row with
            {
                AcceptedAt = terminalAt,
                AcceptedByUserId = acceptedBy.Id,
                RevokedAt = terminalAt,
                TokenHash = null
            },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        string expectedConstraint = scenario switch
        {
            "accepted-without-user" or "user-without-accepted" =>
                "ck_organization_invitations_accepted_by_user",
            "multiple-terminal" =>
                "ck_organization_invitations_terminal_state",
            _ => "ck_organization_invitations_token_state"
        };

        await AssertInsertRejectedAsync(
            row,
            PostgresErrorCodes.CheckViolation,
            expectedConstraint);
    }

    [Theory]
    [InlineData("issued-before-created")]
    [InlineData("expiration-not-after-issued")]
    [InlineData("acceptance-at-expiration")]
    [InlineData("revocation-at-expiration")]
    [InlineData("expired-at-not-expiration")]
    public async Task Database_WithInvalidTemporalShape_RejectsRow(string scenario)
    {
        OrganizationGraph graph = await SeedOrganizationAsync($"time-{scenario}");
        User acceptedBy = await SeedUserAsync(
            "Temporal Recipient",
            $"time.{scenario}@example.test");
        InvitationRow row = ValidRow(graph);
        row = scenario switch
        {
            "issued-before-created" => row with
            {
                TokenIssuedAt = CreatedAt.AddTicks(-1)
            },
            "expiration-not-after-issued" => row with
            {
                ExpiresAt = TokenIssuedAt
            },
            "acceptance-at-expiration" => row with
            {
                AcceptedAt = ExpiresAt,
                AcceptedByUserId = acceptedBy.Id,
                TokenHash = null
            },
            "revocation-at-expiration" => row with
            {
                RevokedAt = ExpiresAt,
                TokenHash = null
            },
            "expired-at-not-expiration" => row with
            {
                ExpiredAt = ExpiresAt.AddMilliseconds(1),
                TokenHash = null
            },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        string expectedConstraint = scenario switch
        {
            "issued-before-created" =>
                "ck_organization_invitations_token_issued_at",
            "expiration-not-after-issued" =>
                "ck_organization_invitations_expiration",
            "acceptance-at-expiration" =>
                "ck_organization_invitations_acceptance_time",
            "revocation-at-expiration" =>
                "ck_organization_invitations_revocation_time",
            _ => "ck_organization_invitations_expired_at"
        };

        await AssertInsertRejectedAsync(
            row,
            PostgresErrorCodes.CheckViolation,
            expectedConstraint);
    }

    [Fact]
    public async Task Database_WithMissingAcceptedByUser_ViolatesForeignKey()
    {
        OrganizationGraph graph = await SeedOrganizationAsync("accepted-user-fk");
        InvitationRow row = ValidRow(graph) with
        {
            AcceptedAt = TokenIssuedAt.AddHours(1),
            AcceptedByUserId = Guid.NewGuid(),
            TokenHash = null
        };

        await AssertInsertRejectedAsync(
            row,
            PostgresErrorCodes.ForeignKeyViolation,
            "fk_organization_invitations_users_accepted_by_user_id");
    }

    [Fact]
    public async Task DeletingCreatorMembership_WithInvitation_IsRestricted()
    {
        OrganizationGraph graph = await SeedOrganizationAsync("delete-creator");
        await SaveInvitationAsync(CreateInvitation(graph, hashSeed: 13));

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        OrganizationMembership membership = await dbContext
            .OrganizationMemberships
            .SingleAsync(candidate => candidate.Id == graph.Membership.Id);
        dbContext.OrganizationMemberships.Remove(membership);

        DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => dbContext.SaveChangesAsync());
        AssertPostgresException(
            exception,
            PostgresErrorCodes.RestrictViolation,
            "fk_organization_invitations_memberships_org_created_by_id");
    }

    [Fact]
    public async Task DeletingAcceptedByUser_WithInvitation_IsRestricted()
    {
        OrganizationGraph graph = await SeedOrganizationAsync("delete-accepted");
        User acceptedBy = await SeedUserAsync(
            "Accepted User",
            "delete.accepted@example.test");
        OrganizationInvitation invitation = CreateInvitation(graph, hashSeed: 14);
        invitation.Accept(acceptedBy.Id, TokenIssuedAt.AddHours(1));
        await SaveInvitationAsync(invitation);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        User trackedUser = await dbContext.Users
            .SingleAsync(candidate => candidate.Id == acceptedBy.Id);
        dbContext.Users.Remove(trackedUser);

        DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => dbContext.SaveChangesAsync());
        AssertPostgresException(
            exception,
            PostgresErrorCodes.RestrictViolation,
            "fk_organization_invitations_users_accepted_by_user_id");
    }

    private async Task<OrganizationGraph> SeedOrganizationAsync(string discriminator)
    {
        var organization = new Organization(
            $"Invitation {discriminator}",
            $"invitation-{discriminator}",
            CreatedAt);
        var user = new User(
            $"Creator {discriminator}",
            $"creator.{discriminator}@example.test",
            CreatedAt);
        var membership = new OrganizationMembership(
            organization.Id,
            user.Id,
            OrganizationRole.Administrator,
            CreatedAt);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.AddRange(organization, user, membership);
        await dbContext.SaveChangesAsync();
        return new OrganizationGraph(organization, user, membership);
    }

    private async Task<User> SeedUserAsync(string name, string email)
    {
        var user = new User(name, email, CreatedAt);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();
        return user;
    }

    private async Task SaveInvitationAsync(OrganizationInvitation invitation)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.OrganizationInvitations.Add(invitation);
        await dbContext.SaveChangesAsync();
    }

    private async Task AssertInsertRejectedAsync(
        InvitationRow row,
        string expectedSqlState,
        string expectedConstraintName)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO organization_invitations
                (id, organization_id, invited_email, role,
                 created_by_membership_id, token_hash, created_at,
                 token_issued_at, expires_at, accepted_at,
                 accepted_by_user_id, revoked_at, expired_at)
            VALUES
                (@id, @organization_id, @invited_email, @role,
                 @created_by_membership_id, @token_hash, @created_at,
                 @token_issued_at, @expires_at, @accepted_at,
                 @accepted_by_user_id, @revoked_at, @expired_at)
            """,
            connection);
        command.Parameters.AddWithValue("id", row.Id);
        command.Parameters.AddWithValue("organization_id", row.OrganizationId);
        command.Parameters.AddWithValue("invited_email", row.InvitedEmail);
        command.Parameters.AddWithValue("role", (int)row.Role);
        command.Parameters.AddWithValue(
            "created_by_membership_id",
            row.CreatedByMembershipId);
        AddNullableParameter(
            command,
            "token_hash",
            NpgsqlDbType.Bytea,
            row.TokenHash);
        command.Parameters.AddWithValue("created_at", row.CreatedAt);
        command.Parameters.AddWithValue("token_issued_at", row.TokenIssuedAt);
        command.Parameters.AddWithValue("expires_at", row.ExpiresAt);
        AddNullableParameter(
            command,
            "accepted_at",
            NpgsqlDbType.TimestampTz,
            row.AcceptedAt);
        AddNullableParameter(
            command,
            "accepted_by_user_id",
            NpgsqlDbType.Uuid,
            row.AcceptedByUserId);
        AddNullableParameter(
            command,
            "revoked_at",
            NpgsqlDbType.TimestampTz,
            row.RevokedAt);
        AddNullableParameter(
            command,
            "expired_at",
            NpgsqlDbType.TimestampTz,
            row.ExpiredAt);

        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
            () => command.ExecuteNonQueryAsync());
        Assert.Equal(expectedSqlState, exception.SqlState);
        Assert.Equal(expectedConstraintName, exception.ConstraintName);
    }

    private static void AddNullableParameter(
        NpgsqlCommand command,
        string name,
        NpgsqlDbType type,
        object? value)
    {
        command.Parameters.Add(new NpgsqlParameter(name, type)
        {
            Value = value ?? DBNull.Value
        });
    }

    private static OrganizationInvitation CreateInvitation(
        OrganizationGraph graph,
        byte hashSeed = 1,
        OrganizationInvitationTokenHash? tokenHash = null)
    {
        return new OrganizationInvitation(
            graph.Organization.Id,
            "invited@example.test",
            OrganizationRole.Member,
            graph.Membership.Id,
            tokenHash ?? CreateTokenHash(hashSeed),
            CreatedAt,
            TokenIssuedAt,
            ExpiresAt);
    }

    private static InvitationRow ValidRow(OrganizationGraph graph)
    {
        return new InvitationRow(
            Guid.NewGuid(),
            graph.Organization.Id,
            "raw.invited@example.test",
            OrganizationRole.Member,
            graph.Membership.Id,
            CreateHashBytes(20),
            CreatedAt,
            TokenIssuedAt,
            ExpiresAt,
            null,
            null,
            null,
            null);
    }

    private static OrganizationInvitationTokenHash CreateTokenHash(byte seed)
    {
        return new OrganizationInvitationTokenHash(CreateHashBytes(seed));
    }

    private static byte[] CreateHashBytes(byte seed)
    {
        return Enumerable.Range(0, 32)
            .Select(index => (byte)(seed + index))
            .ToArray();
    }

    private static void MaterializeTerminalState(
        OrganizationInvitation invitation,
        OrganizationInvitationState state,
        Guid acceptedByUserId)
    {
        switch (state)
        {
            case OrganizationInvitationState.Accepted:
                invitation.Accept(
                    acceptedByUserId,
                    TokenIssuedAt.AddHours(1));
                break;
            case OrganizationInvitationState.Revoked:
                invitation.Revoke(TokenIssuedAt.AddHours(1));
                break;
            case OrganizationInvitationState.Expired:
                invitation.Expire(ExpiresAt);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(state));
        }
    }

    private static void AssertPostgresException(
        DbUpdateException exception,
        string expectedSqlState,
        string expectedConstraintName)
    {
        PostgresException postgresException =
            Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal(expectedSqlState, postgresException.SqlState);
        Assert.Equal(expectedConstraintName, postgresException.ConstraintName);
    }

    private sealed record OrganizationGraph(
        Organization Organization,
        User User,
        OrganizationMembership Membership);

    private sealed record InvitationRow(
        Guid Id,
        Guid OrganizationId,
        string InvitedEmail,
        OrganizationRole Role,
        Guid CreatedByMembershipId,
        byte[]? TokenHash,
        DateTimeOffset CreatedAt,
        DateTimeOffset TokenIssuedAt,
        DateTimeOffset ExpiresAt,
        DateTimeOffset? AcceptedAt,
        Guid? AcceptedByUserId,
        DateTimeOffset? RevokedAt,
        DateTimeOffset? ExpiredAt);
}
