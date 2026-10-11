using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Enma.Api.Contracts.Onboarding;
using Enma.Application.Authentication;
using Enma.Application.Organizations.Invitations;
using Enma.Application.Security;
using Enma.Infrastructure.Persistence;
using Enma.IntegrationTests.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Enma.IntegrationTests.Api.Authentication;

// The anonymous endpoints below are not protected by antiforgery, so a
// cross-site form or text/plain POST (a "simple" request that needs no CORS
// preflight) must never be bound as their JSON body.
[Collection(PostgreSqlCollection.Name)]
public sealed class AnonymousEndpointContentTypeTests : IAsyncLifetime
{
    private const string OwnerEmail = "anonymous-owner@example.test";
    private const string OwnerPassword = "HttpTest!Owner42";
    private const string NewPassword = "HttpTest!Reset42";
    private const string SyntheticToken =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmno-_";

    private readonly PostgreSqlFixture fixture;
    private readonly CountingDeliveries deliveries = new();
    private readonly EnmaApiFactory factory;
    private readonly WebApplicationFactory<Program> application;
    private readonly HttpClient client;

    public AnonymousEndpointContentTypeTests(PostgreSqlFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        this.fixture = fixture;
        factory = new EnmaApiFactory(fixture);
        application = factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ICompromisedPasswordChecker>();
                services.AddSingleton<ICompromisedPasswordChecker>(
                    new SafeCompromisedPasswordChecker());
                services.RemoveAll<IEmailVerificationDelivery>();
                services.AddSingleton<IEmailVerificationDelivery>(deliveries);
                services.RemoveAll<IPasswordRecoveryDelivery>();
                services.AddSingleton<IPasswordRecoveryDelivery>(deliveries);
                services.RemoveAll<IOrganizationInvitationDelivery>();
                services.AddSingleton<IOrganizationInvitationDelivery>(deliveries);
            }));
        client = application.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false
        });
    }

    public static TheoryData<string, string> Requests()
    {
        var data = new TheoryData<string, string>();
        foreach (string path in Bodies.Keys)
        {
            data.Add(path, "text/plain");
            data.Add(path, "application/x-www-form-urlencoded");
        }

        return data;
    }

    private static IReadOnlyDictionary<string, Dictionary<string, string>> Bodies
    {
        get
        {
            return new Dictionary<string, Dictionary<string, string>>
            {
                ["/api/auth/email-verification/resend"] = new()
                {
                    ["email"] = OwnerEmail
                },
                ["/api/auth/email-verification/verify"] = new()
                {
                    ["token"] = SyntheticToken
                },
                ["/api/auth/password-recovery/request"] = new()
                {
                    ["email"] = OwnerEmail
                },
                ["/api/auth/password-recovery/reset"] = new()
                {
                    ["token"] = SyntheticToken,
                    ["newPassword"] = NewPassword
                },
                ["/api/auth/login"] = new()
                {
                    ["email"] = OwnerEmail,
                    ["password"] = OwnerPassword
                },
                ["/api/onboarding/register"] = new()
                {
                    ["organizationName"] = "Cross Site Legal",
                    ["organizationSlug"] = "cross-site-legal",
                    ["ownerName"] = "Cross Site Owner",
                    ["ownerEmail"] = "cross-site-owner@example.test",
                    ["password"] = OwnerPassword
                },
                ["/api/onboarding/register-invited"] = new()
                {
                    ["invitationToken"] = SyntheticToken,
                    ["name"] = "Cross Site Invitee",
                    ["email"] = "cross-site-invitee@example.test",
                    ["password"] = OwnerPassword
                },
                ["/api/invitations/preview"] = new()
                {
                    ["token"] = SyntheticToken
                }
            };
        }
    }

    public async Task InitializeAsync()
    {
        await fixture.ResetDatabaseAsync();

        using HttpResponseMessage registration = await client.PostAsJsonAsync(
            "/api/onboarding/register",
            new RegisterOrganizationOwnerRequest
            {
                OrganizationName = "Existing Legal",
                OrganizationSlug = "existing-legal",
                OwnerName = "Existing Owner",
                OwnerEmail = OwnerEmail,
                Password = OwnerPassword
            });
        Assert.Equal(HttpStatusCode.Created, registration.StatusCode);
        deliveries.Reset();
    }

    public async Task DisposeAsync()
    {
        client.Dispose();
        await application.DisposeAsync();
        await factory.DisposeAsync();
    }

    [Theory]
    [MemberData(nameof(Requests))]
    public async Task Post_NonJsonBody_IsRejectedWithoutSideEffects(
        string path,
        string contentType)
    {
        StateSnapshot before = await CaptureStateAsync();
        using HttpRequestMessage request = new(HttpMethod.Post, path)
        {
            Content = CreateContent(Bodies[path], contentType)
        };

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Equal(before, await CaptureStateAsync());
        Assert.Equal(0, deliveries.Count);
    }

    private static HttpContent CreateContent(
        Dictionary<string, string> fields,
        string contentType)
    {
        return contentType == "text/plain"
            ? new StringContent(
                JsonSerializer.Serialize(fields),
                Encoding.UTF8,
                "text/plain")
            : new FormUrlEncodedContent(fields);
    }

    private async Task<StateSnapshot> CaptureStateAsync()
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();

        return new StateSnapshot(
            await dbContext.Organizations.CountAsync(),
            await dbContext.Users.CountAsync(),
            await dbContext.UserCredentials.CountAsync(),
            await dbContext.OrganizationMemberships.CountAsync(),
            await dbContext.AuthenticationSessions.CountAsync(),
            await dbContext.EmailVerificationChallenges.CountAsync(),
            await dbContext.PasswordRecoveryChallenges.CountAsync(),
            await dbContext.OrganizationInvitations.CountAsync(),
            await dbContext.EmailVerificationSendBudgets.CountAsync(),
            await dbContext.AuditLogs.CountAsync(),
            await dbContext.Users
                .Where(user => user.EmailVerifiedAt != null)
                .CountAsync());
    }

    private sealed record StateSnapshot(
        int Organizations,
        int Users,
        int Credentials,
        int Memberships,
        int Sessions,
        int EmailVerificationChallenges,
        int PasswordRecoveryChallenges,
        int Invitations,
        int SendBudgets,
        int AuditLogs,
        int VerifiedUsers);

    private sealed class CountingDeliveries
        : IEmailVerificationDelivery,
            IPasswordRecoveryDelivery,
            IOrganizationInvitationDelivery
    {
        private int count;

        public int Count => Volatile.Read(ref count);

        public void Reset()
        {
            Volatile.Write(ref count, 0);
        }

        public Task<EmailVerificationDeliveryResult> DeliverAsync(
            string email,
            string rawToken,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref count);
            return Task.FromResult(EmailVerificationDeliveryResult.Delivered);
        }

        public Task<EmailVerificationDeliveryResult> DeliverAsync(
            string email,
            string rawToken,
            string invitationToken,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref count);
            return Task.FromResult(EmailVerificationDeliveryResult.Delivered);
        }

        Task<PasswordRecoveryDeliveryResult> IPasswordRecoveryDelivery.DeliverAsync(
            string email,
            string rawToken,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref count);
            return Task.FromResult(PasswordRecoveryDeliveryResult.Delivered);
        }

        public Task<OrganizationInvitationDeliveryResult> DeliverAsync(
            OrganizationInvitationDeliveryRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref count);
            return Task.FromResult(OrganizationInvitationDeliveryResult.Accepted);
        }
    }

    private sealed class SafeCompromisedPasswordChecker
        : ICompromisedPasswordChecker
    {
        public Task<bool> IsCompromisedAsync(
            string password,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(false);
        }
    }
}
