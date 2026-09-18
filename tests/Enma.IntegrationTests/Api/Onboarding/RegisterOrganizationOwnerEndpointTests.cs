using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Enma.Api.Contracts.Onboarding;
using Enma.Application.Authentication;
using Enma.Application.Security;
using Enma.Domain.Authentication;
using Enma.Domain.Organizations;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Enma.Infrastructure.Security;
using Enma.IntegrationTests.Api;
using Enma.IntegrationTests.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Enma.IntegrationTests.Api.Onboarding;

[Collection(PostgreSqlCollection.Name)]
public sealed class RegisterOrganizationOwnerEndpointTests : IAsyncLifetime
{
    private const string SyntheticPassword = "HttpTest!Owner42";
    private const string InvalidSyntheticPassword = "Short!7";
    private const string SafeDuplicateEmailMessage =
        "A user with the provided email already exists.";
    private const string RequestPath = "/api/onboarding/register";
    private const string InvitedRequestPath = "/api/onboarding/register-invited";
    private const string ResendPath = "/api/auth/email-verification/resend";
    private const string VerifyPath = "/api/auth/email-verification/verify";
    private const string LoginPath = "/api/auth/login";

    private static readonly DateTimeOffset SeedCreatedAt = new(
        2026,
        8,
        5,
        12,
        0,
        0,
        TimeSpan.Zero);

    private readonly PostgreSqlFixture fixture;
    private readonly EnmaApiFactory factory;
    private readonly WebApplicationFactory<Program> testFactory;
    private readonly TestCompromisedPasswordChecker compromisedPasswordChecker;
    private readonly TestEmailVerificationDelivery emailVerificationDelivery;
    private readonly MutableTimeProvider timeProvider = new(SeedCreatedAt);
    private readonly HttpClient client;

    public RegisterOrganizationOwnerEndpointTests(PostgreSqlFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        this.fixture = fixture;
        compromisedPasswordChecker = new TestCompromisedPasswordChecker();
        emailVerificationDelivery = new TestEmailVerificationDelivery();
        factory = new EnmaApiFactory(fixture);
        testFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ICompromisedPasswordChecker>();
                services.AddSingleton<ICompromisedPasswordChecker>(
                    compromisedPasswordChecker);
                services.RemoveAll<IEmailVerificationDelivery>();
                services.AddSingleton<IEmailVerificationDelivery>(
                    emailVerificationDelivery);
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(timeProvider);
            });
        });
        client = testFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
    }

    public Task InitializeAsync()
    {
        compromisedPasswordChecker.Reset();
        emailVerificationDelivery.Reset();
        timeProvider.SetUtcNow(SeedCreatedAt);
        return fixture.ResetDatabaseAsync();
    }

    public async Task DisposeAsync()
    {
        client.Dispose();
        await testFactory.DisposeAsync();
        await factory.DisposeAsync();
    }

    [Fact]
    public async Task InvitedPost_ValidInvite_CreatesOnlyUnverifiedIdentity()
    {
        (Organization organization, string token) = await SeedInvitationAsync(
            "invitee@example.test",
            OrganizationRole.Member);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            InvitedRequestPath,
            new RegisterInvitedUserRequest
            {
                InvitationToken = token,
                Name = "  Invited User  ",
                Email = "  INVITEE@example.test ",
                Password = SyntheticPassword
            });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        RegisterInvitedUserResponse? registration = await response.Content
            .ReadFromJsonAsync<RegisterInvitedUserResponse>();
        Assert.NotNull(registration);
        Assert.True(registration.VerificationEmailSent);
        Assert.Equal("/login", response.Headers.Location?.OriginalString);
        Assert.Equal("invitee@example.test", emailVerificationDelivery.Email);
        Assert.Equal(token, emailVerificationDelivery.InvitationToken);
        Assert.DoesNotContain(
            token,
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Equal(1, await dbContext.Organizations.CountAsync());
        Assert.Equal(organization.Id, await dbContext.Organizations
            .Select(candidate => candidate.Id)
            .SingleAsync());
        Assert.Equal(1, await dbContext.OrganizationMemberships.CountAsync());
        User invitee = await dbContext.Users.SingleAsync(
            user => user.Email == "invitee@example.test");
        Assert.Null(invitee.EmailVerifiedAt);
        Assert.False(await dbContext.OrganizationMemberships.AnyAsync(
            membership => membership.UserId == invitee.Id));
        Assert.True(await dbContext.UserCredentials.AnyAsync(
            credential => credential.UserId == invitee.Id));
        Assert.True(await dbContext.EmailVerificationChallenges.AnyAsync(
            challenge => challenge.UserId == invitee.Id));
    }

    [Fact]
    public async Task InvitedPost_WrongEmail_ReturnsSpecificFailureAndSameTokenRemainsUsable()
    {
        (_, string token) = await SeedInvitationAsync(
            "intended@example.test",
            OrganizationRole.Administrator);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            InvitedRequestPath,
            new RegisterInvitedUserRequest
            {
                InvitationToken = token,
                Name = "Wrong User",
                Email = "wrong@example.test",
                Password = SyntheticPassword
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument problem = JsonDocument.Parse(body);
        Assert.Equal(
            "invited_registration_wrong_recipient",
            problem.RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain("intended@example.test", body, StringComparison.Ordinal);
        Assert.DoesNotContain("wrong@example.test", body, StringComparison.Ordinal);
        Assert.DoesNotContain(token, body, StringComparison.Ordinal);
        Assert.Equal(0, emailVerificationDelivery.CallCount);

        using HttpResponseMessage retry = await client.PostAsJsonAsync(
            InvitedRequestPath,
            new RegisterInvitedUserRequest
            {
                InvitationToken = token,
                Name = "Intended User",
                Email = "intended@example.test",
                Password = SyntheticPassword
            });

        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal(1, emailVerificationDelivery.CallCount);
        Assert.Equal("intended@example.test", emailVerificationDelivery.Email);
        Assert.Equal(token, emailVerificationDelivery.InvitationToken);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Equal(1, await dbContext.Organizations.CountAsync());
        Assert.Equal(2, await dbContext.Users.CountAsync());
        Assert.Equal(1, await dbContext.OrganizationMemberships.CountAsync());
        OrganizationInvitation invitation = await dbContext
            .OrganizationInvitations.SingleAsync();
        Assert.Equal(
            OrganizationInvitationState.Pending,
            invitation.GetState(SeedCreatedAt));
        Assert.NotNull(invitation.TokenHash);
    }

    [Fact]
    public async Task InvitedPost_ExistingUser_ReturnsConflictWithoutDuplicate()
    {
        (_, string token) = await SeedInvitationAsync(
            "existing@example.test",
            OrganizationRole.Member);
        await SeedAsync(new User(
            "Existing User",
            "existing@example.test",
            SeedCreatedAt));

        HttpResponseMessage response = await client.PostAsJsonAsync(
            InvitedRequestPath,
            new RegisterInvitedUserRequest
            {
                InvitationToken = token,
                Name = "Existing User",
                Email = "existing@example.test",
                Password = SyntheticPassword
            });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(0, emailVerificationDelivery.CallCount);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Equal(1, await dbContext.Users.CountAsync(
            user => user.Email == "existing@example.test"));
        Assert.Equal(1, await dbContext.OrganizationMemberships.CountAsync());
    }

    [Fact]
    public async Task InvitedPost_MalformedOrExpiredToken_ReturnSameSafeFailure()
    {
        (_, string expiredToken) = await SeedInvitationAsync(
            "expired@example.test",
            OrganizationRole.Member,
            SeedCreatedAt.AddMinutes(30));
        timeProvider.SetUtcNow(SeedCreatedAt.AddHours(1));
        RegisterInvitedUserRequest CreateRequest(string token) => new()
        {
            InvitationToken = token,
            Name = "Invited User",
            Email = "expired@example.test",
            Password = SyntheticPassword
        };

        HttpResponseMessage malformed = await client.PostAsJsonAsync(
            InvitedRequestPath,
            CreateRequest("invalid"));
        HttpResponseMessage expired = await client.PostAsJsonAsync(
            InvitedRequestPath,
            CreateRequest(expiredToken));

        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, expired.StatusCode);
        Assert.Equal(
            (await malformed.Content.ReadFromJsonAsync<ProblemDetails>())?.Title,
            (await expired.Content.ReadFromJsonAsync<ProblemDetails>())?.Title);
        Assert.Equal(0, emailVerificationDelivery.CallCount);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Equal(1, await dbContext.Users.CountAsync());
        Assert.Equal(1, await dbContext.OrganizationMemberships.CountAsync());
    }

    [Fact]
    public async Task InvitedPost_RevokedOrAcceptedToken_CannotCreateAccount()
    {
        (_, string revokedToken) = await SeedInvitationAsync(
            "revoked@example.test",
            OrganizationRole.Member);
        (_, string acceptedToken) = await SeedInvitationAsync(
            "accepted@example.test",
            OrganizationRole.Administrator);
        var acceptedUser = new User(
            "Accepted User",
            "accepted@example.test",
            SeedCreatedAt);
        acceptedUser.VerifyEmail(SeedCreatedAt);
        await SeedAsync(acceptedUser);

        await using (EnmaDbContext mutationContext = fixture.CreateDbContext())
        {
            OrganizationInvitation revoked = await mutationContext
                .OrganizationInvitations.SingleAsync(invitation =>
                    invitation.InvitedEmail == "revoked@example.test");
            OrganizationInvitation accepted = await mutationContext
                .OrganizationInvitations.SingleAsync(invitation =>
                    invitation.InvitedEmail == "accepted@example.test");
            revoked.Revoke(SeedCreatedAt.AddMinutes(1));
            accepted.Accept(acceptedUser.Id, SeedCreatedAt.AddMinutes(1));
            await mutationContext.SaveChangesAsync();
        }

        RegisterInvitedUserRequest CreateRequest(string token, string email) =>
            new()
            {
                InvitationToken = token,
                Name = "Invited User",
                Email = email,
                Password = SyntheticPassword
            };

        HttpResponseMessage revokedResponse = await client.PostAsJsonAsync(
            InvitedRequestPath,
            CreateRequest(revokedToken, "revoked@example.test"));
        HttpResponseMessage acceptedResponse = await client.PostAsJsonAsync(
            InvitedRequestPath,
            CreateRequest(acceptedToken, "accepted@example.test"));

        Assert.Equal(HttpStatusCode.BadRequest, revokedResponse.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, acceptedResponse.StatusCode);
        Assert.Equal(0, emailVerificationDelivery.CallCount);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.False(await dbContext.UserCredentials.AnyAsync(credential =>
            credential.UserId == acceptedUser.Id));
        Assert.False(await dbContext.Users.AnyAsync(user =>
            user.Email == "revoked@example.test"));
    }

    [Fact]
    public async Task Post_WithValidRequest_ReturnsCreatedResponseAndProtectedLocation()
    {
        RegisterOrganizationOwnerRequest request = CreateValidRequest();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            RequestPath,
            request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(
            "application/json",
            response.Content.Headers.ContentType?.MediaType);
        RegisterOrganizationOwnerResponse? onboarding =
            await response.Content
                .ReadFromJsonAsync<RegisterOrganizationOwnerResponse>();
        Assert.NotNull(onboarding);
        Assert.Equal("Enma Legal", onboarding.OrganizationName);
        Assert.Equal("enma-legal", onboarding.OrganizationSlug);
        Assert.Equal("Ana Silva", onboarding.UserName);
        Assert.Equal("owner@example.com", onboarding.UserEmail);
        Assert.NotEqual(Guid.Empty, onboarding.OrganizationId);
        Assert.NotEqual(Guid.Empty, onboarding.UserId);
        Assert.NotEqual(Guid.Empty, onboarding.MembershipId);
        Assert.Equal(
            3,
            new HashSet<Guid>
            {
                onboarding.OrganizationId,
                onboarding.UserId,
                onboarding.MembershipId
            }.Count);
        Assert.Equal("Owner", onboarding.Role);
        Assert.NotEqual(default, onboarding.CreatedAt);
        Assert.Equal(TimeSpan.Zero, onboarding.CreatedAt.Offset);
        Assert.True(onboarding.VerificationEmailSent);

        Uri? location = response.Headers.Location;
        Assert.NotNull(location);
        string locationPath = location.IsAbsoluteUri
            ? location.AbsolutePath
            : location.OriginalString;
        Assert.Equal(
            $"/api/organizations/{onboarding.OrganizationId}",
            locationPath);

        using HttpResponseMessage getResponse = await client.GetAsync(location);
        Assert.Equal(HttpStatusCode.Unauthorized, getResponse.StatusCode);
        Assert.True(getResponse.Headers.CacheControl?.NoStore);
        Assert.Equal(string.Empty, await getResponse.Content.ReadAsStringAsync());
        Assert.Equal(1, compromisedPasswordChecker.CallCount);
        Assert.True(compromisedPasswordChecker.ReceivedExpectedPassword);
        Assert.Equal(1, emailVerificationDelivery.CallCount);
        Assert.Equal(onboarding.UserEmail, emailVerificationDelivery.Email);
        Assert.False(string.IsNullOrWhiteSpace(emailVerificationDelivery.RawToken));
    }

    [Fact]
    public async Task Post_WhenVerificationDeliveryFails_ReturnsCreatedAndKeepsChallenge()
    {
        emailVerificationDelivery.Result = EmailVerificationDeliveryResult.Failed;

        HttpResponseMessage response = await client.PostAsJsonAsync(
            RequestPath,
            CreateValidRequest());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        RegisterOrganizationOwnerResponse? onboarding = await response.Content
            .ReadFromJsonAsync<RegisterOrganizationOwnerResponse>();
        Assert.NotNull(onboarding);
        Assert.Equal("owner@example.com", onboarding.UserEmail);
        Assert.False(onboarding.VerificationEmailSent);
        Assert.Equal(1, emailVerificationDelivery.CallCount);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Equal(1, await dbContext.Organizations.CountAsync());
        Assert.Equal(1, await dbContext.Users.CountAsync());
        Assert.Equal(1, await dbContext.UserCredentials.CountAsync());
        Assert.Equal(1, await dbContext.OrganizationMemberships.CountAsync());
        Assert.Equal(1, await dbContext.EmailVerificationChallenges.CountAsync());
    }

    [Fact]
    public async Task Post_WhenInitialDeliveryFails_ResendRotatesAndRecoversAccount()
    {
        emailVerificationDelivery.Result = EmailVerificationDeliveryResult.Failed;

        using HttpResponseMessage registrationResponse = await client.PostAsJsonAsync(
            RequestPath,
            CreateValidRequest());

        Assert.Equal(HttpStatusCode.Created, registrationResponse.StatusCode);
        RegisterOrganizationOwnerResponse? onboarding = await registrationResponse
            .Content
            .ReadFromJsonAsync<RegisterOrganizationOwnerResponse>();
        Assert.NotNull(onboarding);
        Assert.False(onboarding.VerificationEmailSent);
        string firstToken = Assert.IsType<string>(
            emailVerificationDelivery.RawToken);

        timeProvider.SetUtcNow(
            SeedCreatedAt.Add(EmailVerificationPolicy.ResendCooldown));
        emailVerificationDelivery.Result = EmailVerificationDeliveryResult.Delivered;

        using HttpResponseMessage resendResponse = await client.PostAsJsonAsync(
            ResendPath,
            new { Email = "  OWNER@EXAMPLE.COM  " });

        Assert.Equal(HttpStatusCode.Accepted, resendResponse.StatusCode);
        Assert.True(resendResponse.Headers.CacheControl?.NoStore);
        Assert.Equal(string.Empty, await resendResponse.Content.ReadAsStringAsync());
        Assert.Equal(2, emailVerificationDelivery.CallCount);
        string secondToken = Assert.IsType<string>(
            emailVerificationDelivery.RawToken);
        Assert.NotEqual(firstToken, secondToken);

        using HttpResponseMessage oldTokenResponse = await client.PostAsJsonAsync(
            VerifyPath,
            new { Token = firstToken });
        Assert.Equal(HttpStatusCode.BadRequest, oldTokenResponse.StatusCode);

        using HttpResponseMessage newTokenResponse = await client.PostAsJsonAsync(
            VerifyPath,
            new { Token = secondToken });
        Assert.Equal(HttpStatusCode.NoContent, newTokenResponse.StatusCode);

        using HttpResponseMessage loginResponse = await client.PostAsJsonAsync(
            LoginPath,
            new { Email = "owner@example.com", Password = SyntheticPassword });
        Assert.Equal(HttpStatusCode.NoContent, loginResponse.StatusCode);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        User user = await dbContext.Users
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == onboarding.UserId);
        Assert.NotNull(user.EmailVerifiedAt);
        Assert.Equal(
            0,
            await dbContext.EmailVerificationChallenges.CountAsync(
                candidate => candidate.UserId == onboarding.UserId));
    }

    [Fact]
    public async Task Post_WithValidRequest_PersistsCompleteOnboardingAndVerifiableCredential()
    {
        RegisterOrganizationOwnerRequest request = CreateValidRequest();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            RequestPath,
            request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        RegisterOrganizationOwnerResponse? onboarding =
            await response.Content
                .ReadFromJsonAsync<RegisterOrganizationOwnerResponse>();
        Assert.NotNull(onboarding);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Organization organization = await dbContext.Organizations
            .AsNoTracking()
            .SingleAsync();
        User user = await dbContext.Users
            .AsNoTracking()
            .SingleAsync();
        UserCredential credential = await dbContext.UserCredentials
            .AsNoTracking()
            .SingleAsync();
        OrganizationMembership membership =
            await dbContext.OrganizationMemberships
                .AsNoTracking()
                .SingleAsync();
        EmailVerificationChallenge challenge = await dbContext
            .EmailVerificationChallenges
            .AsNoTracking()
            .SingleAsync();

        Assert.Equal(onboarding.OrganizationId, organization.Id);
        Assert.Equal("Enma Legal", organization.Name);
        Assert.Equal("enma-legal", organization.Slug);
        Assert.True(organization.IsActive);
        Assert.Equal(onboarding.UserId, user.Id);
        Assert.Equal("Ana Silva", user.Name);
        Assert.Equal("owner@example.com", user.Email);
        Assert.True(user.IsActive);
        Assert.Equal(onboarding.MembershipId, membership.Id);
        Assert.Equal(organization.Id, membership.OrganizationId);
        Assert.Equal(user.Id, membership.UserId);
        Assert.Equal(OrganizationRole.Owner, membership.Role);
        Assert.True(membership.IsActive);
        Assert.Equal(user.Id, credential.UserId);
        Assert.False(string.IsNullOrWhiteSpace(credential.PasswordHash));
        Assert.NotEqual(request.Password, credential.PasswordHash);
        Assert.Equal(user.Id, challenge.UserId);
        Assert.Equal(user.Email, challenge.EmailAtIssue);
        Assert.Equal(
            EmailVerificationPolicy.TokenLifetime,
            challenge.ExpiresAt - challenge.CreatedAt);
        Assert.Equal(1, emailVerificationDelivery.CallCount);
        Assert.Equal(user.Email, emailVerificationDelivery.Email);
        string rawToken = Assert.IsType<string>(emailVerificationDelivery.RawToken);

        using IServiceScope scope = testFactory.Services.CreateScope();
        IPasswordHasher passwordHasher =
            scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        Assert.Equal(
            PasswordVerificationResult.Success,
            passwordHasher.VerifyHashedPassword(
                credential.PasswordHash,
                request.Password));
        IEmailVerificationTokenService tokenService = scope.ServiceProvider
            .GetRequiredService<IEmailVerificationTokenService>();
        Assert.True(tokenService.TryHashToken(rawToken, out var deliveredTokenHash));
        Assert.Equal(deliveredTokenHash, challenge.TokenHash);
        Assert.Equal(1, compromisedPasswordChecker.CallCount);
        Assert.True(compromisedPasswordChecker.ReceivedExpectedPassword);
    }

    [Fact]
    public async Task Post_WithInvalidOrganizationName_ReturnsBadRequestWithoutWrites()
    {
        RegisterOrganizationOwnerRequest request = CreateValidRequest(
            organizationName: "   ");

        HttpResponseMessage response = await client.PostAsJsonAsync(
            RequestPath,
            request);

        await AssertProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            "Invalid onboarding request");
        Assert.Equal(0, compromisedPasswordChecker.CallCount);
        await AssertAllTablesEmptyAsync();
    }

    [Fact]
    public async Task Post_WithInvalidOwnerEmail_ReturnsBadRequestWithoutWrites()
    {
        RegisterOrganizationOwnerRequest request = CreateValidRequest(
            ownerEmail: "invalid-email");

        HttpResponseMessage response = await client.PostAsJsonAsync(
            RequestPath,
            request);

        await AssertProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            "Invalid onboarding request");
        Assert.Equal(0, compromisedPasswordChecker.CallCount);
        await AssertAllTablesEmptyAsync();
    }

    [Fact]
    public async Task Post_WithInvalidPassword_ReturnsBadRequestWithoutWritesOrPasswordExposure()
    {
        RegisterOrganizationOwnerRequest request = CreateValidRequest(
            password: InvalidSyntheticPassword);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            RequestPath,
            request);

        (ProblemDetails problemDetails, string rawResponse) =
            await AssertProblemAsync(
                response,
                HttpStatusCode.BadRequest,
                "Invalid onboarding request");
        Assert.Contains(PasswordPolicyErrors.PasswordTooShort, problemDetails.Detail);
        Assert.DoesNotContain(
            request.Password,
            rawResponse,
            StringComparison.Ordinal);
        Assert.Equal(0, compromisedPasswordChecker.CallCount);
        await AssertAllTablesEmptyAsync();
    }

    [Fact]
    public async Task Post_WithExistingSlug_ReturnsConflictWithoutNewWrites()
    {
        Organization seededOrganization = new(
            "Existing Legal",
            "enma-legal",
            SeedCreatedAt);
        await SeedAsync(seededOrganization);
        RegisterOrganizationOwnerRequest request = CreateValidRequest();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            RequestPath,
            request);

        await AssertProblemAsync(
            response,
            HttpStatusCode.Conflict,
            "Onboarding conflict");
        Assert.Equal(0, compromisedPasswordChecker.CallCount);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Organization organization = await dbContext.Organizations
            .AsNoTracking()
            .SingleAsync();
        Assert.Equal(seededOrganization.Id, organization.Id);
        Assert.Equal(0, await dbContext.Users.CountAsync());
        Assert.Equal(0, await dbContext.UserCredentials.CountAsync());
        Assert.Equal(0, await dbContext.OrganizationMemberships.CountAsync());
        Assert.Equal(0, await dbContext.EmailVerificationChallenges.CountAsync());
    }

    [Fact]
    public async Task Post_WithExistingEmail_ReturnsConflictWithoutNewWritesOrEmailExposure()
    {
        User seededUser = new(
            "Existing User",
            "owner@example.com",
            SeedCreatedAt);
        await SeedAsync(seededUser);
        RegisterOrganizationOwnerRequest request = CreateValidRequest(
            organizationSlug: "new-legal",
            ownerEmail: "owner@example.com");

        HttpResponseMessage response = await client.PostAsJsonAsync(
            RequestPath,
            request);

        (ProblemDetails problemDetails, string rawResponse) =
            await AssertProblemAsync(
                response,
                HttpStatusCode.Conflict,
                "Onboarding conflict");
        Assert.Equal(SafeDuplicateEmailMessage, problemDetails.Detail);
        Assert.DoesNotContain(
            request.OwnerEmail,
            rawResponse,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, compromisedPasswordChecker.CallCount);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        User user = await dbContext.Users.AsNoTracking().SingleAsync();
        Assert.Equal(seededUser.Id, user.Id);
        Assert.Equal(0, await dbContext.Organizations.CountAsync());
        Assert.Equal(0, await dbContext.UserCredentials.CountAsync());
        Assert.Equal(0, await dbContext.OrganizationMemberships.CountAsync());
        Assert.Equal(0, await dbContext.EmailVerificationChallenges.CountAsync());
    }

    [Fact]
    public async Task Post_ResponseContract_DoesNotExposePasswordOrCredentialData()
    {
        RegisterOrganizationOwnerRequest request = CreateValidRequest();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            RequestPath,
            request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        string rawResponse = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("password", rawResponse, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("passwordHash", rawResponse, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", rawResponse, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "passwordChangedAt",
            rawResponse,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            request.Password,
            rawResponse,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            Assert.IsType<string>(emailVerificationDelivery.RawToken),
            rawResponse,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "verificationUrl",
            rawResponse,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "challengeId",
            rawResponse,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("smtp", rawResponse, StringComparison.OrdinalIgnoreCase);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        UserCredential credential = await dbContext.UserCredentials
            .AsNoTracking()
            .SingleAsync();
        Assert.DoesNotContain(
            credential.PasswordHash,
            rawResponse,
            StringComparison.Ordinal);

        string requestDescription = Assert.IsType<string>(request.ToString());
        Assert.DoesNotContain(
            request.Password,
            requestDescription,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            request.OwnerEmail,
            requestDescription,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            request.OrganizationName,
            requestDescription,
            StringComparison.Ordinal);
        Assert.Equal(1, compromisedPasswordChecker.CallCount);
        Assert.True(compromisedPasswordChecker.ReceivedExpectedPassword);
    }

    [Fact]
    public async Task Post_WithCompromisedPassword_ReturnsBadRequestWithoutWritesOrPasswordExposure()
    {
        compromisedPasswordChecker.IsCompromised = true;
        RegisterOrganizationOwnerRequest request = CreateValidRequest();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            RequestPath,
            request);

        (ProblemDetails problemDetails, string rawResponse) =
            await AssertProblemAsync(
                response,
                HttpStatusCode.BadRequest,
                "Invalid onboarding request");
        Assert.Equal(
            "The provided password has appeared in a known data breach and cannot be used.",
            problemDetails.Detail);
        Assert.Equal(1, compromisedPasswordChecker.CallCount);
        Assert.DoesNotContain(request.Password, rawResponse, StringComparison.Ordinal);
        Assert.DoesNotContain("passwordHash", rawResponse, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", rawResponse, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SHA-1", rawResponse, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "Pwned Passwords",
            rawResponse,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "api.pwnedpasswords.com",
            rawResponse,
            StringComparison.OrdinalIgnoreCase);
        await AssertAllTablesEmptyAsync();
    }

    [Fact]
    public async Task Post_WhenPasswordScreeningIsUnavailable_ReturnsServiceUnavailableWithoutWritesOrSensitiveDetails()
    {
        const string syntheticLookupPrefix = "A1B2C";
        const string syntheticLookupSuffix =
            "0123456789ABCDEF0123456789ABCDEFABC";
        const string syntheticProviderResponseDetail =
            "synthetic-provider-response-detail-9f4c";
        const string syntheticInternalDiagnosticMarker =
            "synthetic-internal-diagnostic-marker-7e2a";
        string syntheticProviderUri =
            $"https://synthetic-password-screening.invalid/range/{syntheticLookupPrefix}";
        string syntheticCompleteHash =
            syntheticLookupPrefix + syntheticLookupSuffix;
        string syntheticDiagnosticMessage =
            $"ProviderUri={syntheticProviderUri}; " +
            $"LookupPrefix={syntheticLookupPrefix}; " +
            $"LookupSuffix={syntheticLookupSuffix}; " +
            $"CompleteHash={syntheticCompleteHash}; " +
            $"ProviderResponse={syntheticProviderResponseDetail}; " +
            $"InternalDiagnostic={syntheticInternalDiagnosticMarker}";
        compromisedPasswordChecker.ExceptionToThrow =
            new CompromisedPasswordCheckUnavailableException(
                new InvalidOperationException(syntheticDiagnosticMessage));
        RegisterOrganizationOwnerRequest request = CreateValidRequest();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            RequestPath,
            request);

        (ProblemDetails problemDetails, string rawResponse) =
            await AssertProblemAsync(
                response,
                HttpStatusCode.ServiceUnavailable,
                "Password screening unavailable");
        Assert.Equal(
            "Password compromise screening is temporarily unavailable.",
            problemDetails.Detail);
        Assert.Equal(1, compromisedPasswordChecker.CallCount);
        Assert.DoesNotContain(request.Password, rawResponse, StringComparison.Ordinal);
        Assert.DoesNotContain("HIBP", rawResponse, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "Pwned Passwords",
            rawResponse,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "api.pwnedpasswords.com",
            rawResponse,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SHA-1", rawResponse, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            syntheticProviderUri,
            rawResponse,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            syntheticLookupPrefix,
            rawResponse,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            syntheticLookupSuffix,
            rawResponse,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            syntheticCompleteHash,
            rawResponse,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            syntheticProviderResponseDetail,
            rawResponse,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            syntheticInternalDiagnosticMarker,
            rawResponse,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            nameof(InvalidOperationException),
            rawResponse,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "System.InvalidOperationException",
            rawResponse,
            StringComparison.OrdinalIgnoreCase);
        await AssertAllTablesEmptyAsync();
    }

    private static RegisterOrganizationOwnerRequest CreateValidRequest(
        string organizationName = "  Enma Legal  ",
        string organizationSlug = "  ENMA-LEGAL  ",
        string ownerEmail = "  OWNER@EXAMPLE.COM  ",
        string password = SyntheticPassword)
    {
        return new RegisterOrganizationOwnerRequest
        {
            OrganizationName = organizationName,
            OrganizationSlug = organizationSlug,
            OwnerName = "  Ana Silva  ",
            OwnerEmail = ownerEmail,
            Password = password
        };
    }

    private static async Task<(ProblemDetails ProblemDetails, string RawResponse)>
        AssertProblemAsync(
            HttpResponseMessage response,
            HttpStatusCode expectedStatusCode,
            string expectedTitle)
    {
        Assert.Equal(expectedStatusCode, response.StatusCode);
        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);
        string rawResponse = await response.Content.ReadAsStringAsync();
        ProblemDetails? problemDetails = JsonSerializer.Deserialize<ProblemDetails>(
            rawResponse,
            JsonSerializerOptions.Web);
        Assert.NotNull(problemDetails);
        Assert.Equal((int)expectedStatusCode, problemDetails.Status);
        Assert.Equal(expectedTitle, problemDetails.Title);
        Assert.Equal(RequestPath, problemDetails.Instance);
        Assert.True(
            problemDetails.Extensions.TryGetValue("traceId", out object? traceId));
        Assert.False(string.IsNullOrWhiteSpace(traceId?.ToString()));

        return (problemDetails, rawResponse);
    }

    private async Task AssertAllTablesEmptyAsync()
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Equal(0, await dbContext.Organizations.CountAsync());
        Assert.Equal(0, await dbContext.Users.CountAsync());
        Assert.Equal(0, await dbContext.UserCredentials.CountAsync());
        Assert.Equal(0, await dbContext.OrganizationMemberships.CountAsync());
        Assert.Equal(0, await dbContext.EmailVerificationChallenges.CountAsync());
    }

    private async Task<(Organization Organization, string RawToken)>
        SeedInvitationAsync(
            string email,
            OrganizationRole role,
            DateTimeOffset? expiresAt = null)
    {
        string suffix = Guid.NewGuid().ToString("N");
        var organization = new Organization(
            "Inviting Organization",
            $"inviting-{suffix}",
            SeedCreatedAt);
        var owner = new User(
            "Inviting Owner",
            $"owner-{suffix}@example.test",
            SeedCreatedAt);
        var membership = new OrganizationMembership(
            organization.Id,
            owner.Id,
            OrganizationRole.Owner,
            SeedCreatedAt);
        var tokenService = new CryptographicOrganizationInvitationTokenService();
        string rawToken = tokenService.GenerateToken(out var tokenHash);
        var invitation = new OrganizationInvitation(
            organization.Id,
            email,
            role,
            membership.Id,
            tokenHash,
            SeedCreatedAt,
            SeedCreatedAt,
            expiresAt ?? SeedCreatedAt.AddDays(1));
        await SeedAsync(organization, owner, membership, invitation);
        return (organization, rawToken);
    }

    private async Task SeedAsync(params object[] entities)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.AddRange(entities);
        await dbContext.SaveChangesAsync();
    }

    private sealed class TestCompromisedPasswordChecker
        : ICompromisedPasswordChecker
    {
        public int CallCount { get; private set; }

        public bool ReceivedExpectedPassword { get; private set; }

        public bool IsCompromised { get; set; }

        public CompromisedPasswordCheckUnavailableException? ExceptionToThrow { get; set; }

        public Task<bool> IsCompromisedAsync(
            string password,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            ReceivedExpectedPassword = password == SyntheticPassword;
            Assert.True(ReceivedExpectedPassword);

            if (ExceptionToThrow is not null)
            {
                throw ExceptionToThrow;
            }

            return Task.FromResult(IsCompromised);
        }

        public void Reset()
        {
            CallCount = 0;
            ReceivedExpectedPassword = false;
            IsCompromised = false;
            ExceptionToThrow = null;
        }
    }

    private sealed class TestEmailVerificationDelivery : IEmailVerificationDelivery
    {
        public EmailVerificationDeliveryResult Result { get; set; } =
            EmailVerificationDeliveryResult.Delivered;

        public int CallCount { get; private set; }

        public string? Email { get; private set; }

        public string? RawToken { get; private set; }

        public string? InvitationToken { get; private set; }

        public Task<EmailVerificationDeliveryResult> DeliverAsync(
            string email,
            string rawToken,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Email = email;
            RawToken = rawToken;
            return Task.FromResult(Result);
        }

        public Task<EmailVerificationDeliveryResult> DeliverAsync(
            string email,
            string rawToken,
            string invitationToken,
            CancellationToken cancellationToken = default)
        {
            InvitationToken = invitationToken;
            return DeliverAsync(email, rawToken, cancellationToken);
        }

        public void Reset()
        {
            CallCount = 0;
            Email = null;
            RawToken = null;
            InvitationToken = null;
            Result = EmailVerificationDeliveryResult.Delivered;
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset currentUtcNow = utcNow;

        public override DateTimeOffset GetUtcNow()
        {
            return currentUtcNow;
        }

        public void SetUtcNow(DateTimeOffset value)
        {
            currentUtcNow = value;
        }
    }
}
