using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Enma.Api.Contracts.Finance;
using Enma.Application.Authentication;
using Enma.Domain.Authentication;
using Enma.Domain.Clients;
using Enma.Domain.Organizations;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Enma.IntegrationTests.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Net.Http.Headers;

namespace Enma.IntegrationTests.Api.Finance;

[Collection(PostgreSqlCollection.Name)]
public sealed class FinanceEndpointTests : IAsyncLifetime
{
    private const string CsrfPath = "/api/auth/csrf";
    private const string SessionCookieName = "__Host-enma_session";
    private const string AntiforgeryCookieName = "__Host-enma_csrf";
    private const string CsrfHeaderName = "X-CSRF-TOKEN";
    private const string PasswordHash =
        "synthetic-finance-endpoint-password-hash";
    private static readonly DateTimeOffset Now = new(
        2026, 9, 7, 20, 0, 0, TimeSpan.Zero);

    private readonly PostgreSqlFixture fixture;
    private readonly EnmaApiFactory factory;
    private readonly HttpClient client;

    public FinanceEndpointTests(PostgreSqlFixture fixture)
    {
        this.fixture = fixture;
        factory = new EnmaApiFactory(fixture, services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));
        });
        client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false
        });
    }

    public Task InitializeAsync() => fixture.ResetDatabaseAsync();

    public async Task DisposeAsync()
    {
        client.Dispose();
        await factory.DisposeAsync();
    }

    [Fact]
    public void Contracts_ExposeOnlyApprovedFields()
    {
        Assert.Equal(
            [
                nameof(CreatePaymentPlanRequest.ClientId),
                nameof(CreatePaymentPlanRequest.TotalAmount),
                nameof(CreatePaymentPlanRequest.InstallmentCount),
                nameof(CreatePaymentPlanRequest.FirstDueDate)
            ],
            GetPropertyNames<CreatePaymentPlanRequest>());
        Assert.Equal(
            [nameof(CreatePaymentPlanResponse.PaymentPlanId)],
            GetPropertyNames<CreatePaymentPlanResponse>());

        string[] forbiddenNames =
        [
            "OrganizationId",
            "MembershipId",
            "Role",
            "CreatedAt",
            "Audit",
            "Installments"
        ];

        foreach (Type contractType in new[]
        {
            typeof(CreatePaymentPlanRequest),
            typeof(CreatePaymentPlanResponse)
        })
        {
            Assert.DoesNotContain(
                contractType.GetProperties(),
                property => forbiddenNames.Contains(
                    property.Name,
                    StringComparer.Ordinal));
        }
    }

    [Fact]
    public async Task Create_Anonymous_ReturnsUnauthorizedBeforeAntiforgery()
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            GetPaymentPlansPath(Guid.NewGuid()),
            ValidBody(Guid.NewGuid()));

        await AssertEmptyResponseAsync(response, HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData(OrganizationRole.Owner, HttpStatusCode.Created)]
    [InlineData(OrganizationRole.Administrator, HttpStatusCode.Created)]
    [InlineData(OrganizationRole.Member, HttpStatusCode.Forbidden)]
    public async Task Create_CurrentFinanceRole_EnforcesAndReturnsCanonicalResource(
        OrganizationRole role,
        HttpStatusCode expectedStatus)
    {
        User user = CreateUser($"role-{role}");
        Organization organization = CreateOrganization($"Role {role}");
        OrganizationMembership membership = CreateMembership(
            user,
            organization,
            role);
        var relatedClient = new Client(
            organization.Id,
            $"{role} Finance Client",
            Now.AddDays(-1));
        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            [organization],
            [membership],
            [relatedClient]);
        CsrfPair csrf = await GetCsrfPairAsync(rawHandle);

        using HttpResponseMessage response = await SendMutationAsync(
            GetPaymentPlansPath(organization.Id),
            rawHandle,
            csrf,
            new
            {
                clientId = relatedClient.Id,
                totalAmount = 100.01m,
                installmentCount = 3,
                firstDueDate = new DateOnly(2027, 1, 31),
                organizationId = Guid.NewGuid(),
                membershipId = Guid.NewGuid(),
                role = OrganizationRole.Owner,
                createdAt = Now.AddYears(-5)
            });

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        if (expectedStatus == HttpStatusCode.Created)
        {
            CreatePaymentPlanResponse? created = await response.Content
                .ReadFromJsonAsync<CreatePaymentPlanResponse>();
            Assert.NotNull(created);
            Assert.NotEqual(Guid.Empty, created.PaymentPlanId);
            Assert.Equal(
                $"{GetPaymentPlansPath(organization.Id)}/" +
                $"{created.PaymentPlanId:D}",
                response.Headers.Location?.OriginalString);
            using JsonDocument document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync());
            Assert.Equal(
                ["paymentPlanId"],
                document.RootElement
                    .EnumerateObject()
                    .Select(property => property.Name)
                    .ToArray());

            Enma.Domain.Finance.ClientPaymentPlan persisted =
                await dbContext.ClientPaymentPlans
                    .AsNoTracking()
                    .SingleAsync(candidate =>
                        candidate.Id == created.PaymentPlanId);
            Assert.Equal(organization.Id, persisted.OrganizationId);
            Assert.Equal(relatedClient.Id, persisted.ClientId);
            Assert.Equal(Now, persisted.CreatedAt);
            Assert.Equal(
                3,
                await dbContext.PaymentInstallments.CountAsync(item =>
                    item.PaymentPlanId == created.PaymentPlanId));
        }
        else
        {
            await AssertEmptyResponseAsync(response, HttpStatusCode.Forbidden);
            await AssertNoWritesAsync(dbContext);
        }
    }

    [Fact]
    public async Task Create_UnavailableClients_ReturnSameNotFoundWithoutWrites()
    {
        User user = CreateUser("client-oracle");
        Organization organizationA = CreateOrganization("Client A");
        Organization organizationB = CreateOrganization("Client B");
        OrganizationMembership membership = CreateMembership(
            user,
            organizationA,
            OrganizationRole.Owner);
        var inactiveClient = new Client(
            organizationA.Id,
            "Inactive Client",
            Now.AddDays(-1));
        inactiveClient.Deactivate();
        var foreignClient = new Client(
            organizationB.Id,
            "Foreign Client",
            Now.AddDays(-1));
        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            [organizationA, organizationB],
            [membership],
            [inactiveClient, foreignClient]);
        CsrfPair csrf = await GetCsrfPairAsync(rawHandle);

        using HttpResponseMessage missing = await SendMutationAsync(
            GetPaymentPlansPath(organizationA.Id),
            rawHandle,
            csrf,
            ValidBody(Guid.NewGuid()));
        using HttpResponseMessage inactive = await SendMutationAsync(
            GetPaymentPlansPath(organizationA.Id),
            rawHandle,
            csrf,
            ValidBody(inactiveClient.Id));
        using HttpResponseMessage foreign = await SendMutationAsync(
            GetPaymentPlansPath(organizationA.Id),
            rawHandle,
            csrf,
            ValidBody(foreignClient.Id));

        await AssertEmptyResponseAsync(missing, HttpStatusCode.NotFound);
        await AssertEmptyResponseAsync(inactive, HttpStatusCode.NotFound);
        await AssertEmptyResponseAsync(foreign, HttpStatusCode.NotFound);
        Assert.Equal(
            await missing.Content.ReadAsStringAsync(),
            await inactive.Content.ReadAsStringAsync());
        Assert.Equal(
            await missing.Content.ReadAsStringAsync(),
            await foreign.Content.ReadAsStringAsync());
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        await AssertNoWritesAsync(dbContext);
    }

    [Fact]
    public async Task Create_InvalidInputsAndMalformedJson_ReturnBadRequestWithoutWrites()
    {
        User user = CreateUser("invalid-input");
        Organization organization = CreateOrganization("Invalid Input");
        OrganizationMembership membership = CreateMembership(
            user,
            organization,
            OrganizationRole.Owner);
        var relatedClient = new Client(
            organization.Id,
            "Validation Client",
            Now.AddDays(-1));
        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            [organization],
            [membership],
            [relatedClient]);
        CsrfPair csrf = await GetCsrfPairAsync(rawHandle);
        object[] invalidBodies =
        [
            ValidBody(Guid.Empty),
            new { clientId = relatedClient.Id, totalAmount = 0m, installmentCount = 1, firstDueDate = "2027-01-01" },
            new { clientId = relatedClient.Id, totalAmount = 1.001m, installmentCount = 1, firstDueDate = "2027-01-01" },
            new { clientId = relatedClient.Id, totalAmount = 10_000_000_000_000_000m, installmentCount = 1, firstDueDate = "2027-01-01" },
            new { clientId = relatedClient.Id, totalAmount = 1m, installmentCount = 0, firstDueDate = "2027-01-01" },
            new { clientId = relatedClient.Id, totalAmount = 121m, installmentCount = 121, firstDueDate = "2027-01-01" },
            new { clientId = relatedClient.Id, totalAmount = 0.01m, installmentCount = 2, firstDueDate = "2027-01-01" },
            new { clientId = relatedClient.Id, totalAmount = 1m, installmentCount = 1, firstDueDate = "0001-01-01" },
            new { clientId = relatedClient.Id, totalAmount = 2m, installmentCount = 2, firstDueDate = "9999-12-31" }
        ];

        foreach (object body in invalidBodies)
        {
            using HttpResponseMessage response = await SendMutationAsync(
                GetPaymentPlansPath(organization.Id),
                rawHandle,
                csrf,
                body);
            await AssertSafeBadRequestAsync(response);
        }

        using HttpResponseMessage malformed = await SendMalformedJsonAsync(
            GetPaymentPlansPath(organization.Id),
            rawHandle,
            csrf);
        await AssertSafeBadRequestAsync(malformed);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        await AssertNoWritesAsync(dbContext);
    }

    [Fact]
    public async Task Create_MissingAntiforgery_ReturnsBadRequestWithoutWrites()
    {
        User user = CreateUser("csrf");
        Organization organization = CreateOrganization("Csrf");
        OrganizationMembership membership = CreateMembership(
            user,
            organization,
            OrganizationRole.Owner);
        var relatedClient = new Client(
            organization.Id,
            "Csrf Client",
            Now.AddDays(-1));
        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            [organization],
            [membership],
            [relatedClient]);

        using HttpResponseMessage response = await SendMutationAsync(
            GetPaymentPlansPath(organization.Id),
            rawHandle,
            csrf: null,
            ValidBody(relatedClient.Id));

        await AssertEmptyResponseAsync(response, HttpStatusCode.BadRequest);
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        await AssertNoWritesAsync(dbContext);
    }

    private static string[] GetPropertyNames<T>()
    {
        return typeof(T).GetProperties().Select(property => property.Name).ToArray();
    }

    private static object ValidBody(Guid clientId)
    {
        return new
        {
            clientId,
            totalAmount = 100.01m,
            installmentCount = 3,
            firstDueDate = new DateOnly(2027, 1, 31)
        };
    }

    private static User CreateUser(string marker)
    {
        var user = new User(
            $"Finance HTTP {marker}",
            $"finance-http-{marker}-{Guid.NewGuid():N}@example.test",
            Now.AddHours(-2));
        user.VerifyEmail(Now.AddHours(-1));
        return user;
    }

    private static Organization CreateOrganization(string marker)
    {
        return new Organization(
            $"{marker} Finance",
            $"finance-{marker.ToLowerInvariant().Replace(' ', '-')}-" +
            $"{Guid.NewGuid():N}",
            Now.AddHours(-2));
    }

    private static OrganizationMembership CreateMembership(
        User user,
        Organization organization,
        OrganizationRole role)
    {
        return new OrganizationMembership(
            organization.Id,
            user.Id,
            role,
            Now.AddHours(-1));
    }

    private async Task<string> SeedAuthenticatedUserAsync(
        User user,
        IReadOnlyCollection<Organization> organizations,
        IReadOnlyCollection<OrganizationMembership> memberships,
        IReadOnlyCollection<Client> clients)
    {
        IAuthenticationSessionHandleService handleService = factory.Services
            .GetRequiredService<IAuthenticationSessionHandleService>();
        string rawHandle = handleService.GenerateHandle(out var secretHash);
        var credential = new UserCredential(
            user.Id,
            PasswordHash,
            Now.AddHours(-1));
        var session = new AuthenticationSession(
            user.Id,
            secretHash,
            credential.CredentialVersion,
            Now.AddMinutes(-30),
            Now.AddMinutes(10),
            Now.AddHours(2));

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.Organizations.AddRange(organizations);
        dbContext.Users.Add(user);
        dbContext.UserCredentials.Add(credential);
        dbContext.OrganizationMemberships.AddRange(memberships);
        dbContext.Clients.AddRange(clients);
        dbContext.AuthenticationSessions.Add(session);
        await dbContext.SaveChangesAsync();

        return rawHandle;
    }

    private async Task<CsrfPair> GetCsrfPairAsync(string rawHandle)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, CsrfPath);
        request.Headers.Add(
            HeaderNames.Cookie,
            $"{SessionCookieName}={rawHandle}");
        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        CsrfResponse? result = await response.Content
            .ReadFromJsonAsync<CsrfResponse>();
        Assert.NotNull(result);
        SetCookieHeaderValue cookie = Assert.Single(
            ParseSetCookies(response),
            candidate => string.Equals(
                candidate.Name.ToString(),
                AntiforgeryCookieName,
                StringComparison.Ordinal));
        return new CsrfPair(result.RequestToken, cookie.Value.ToString());
    }

    private async Task<HttpResponseMessage> SendMutationAsync(
        string path,
        string rawHandle,
        CsrfPair? csrf,
        object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        AddCookiesAndCsrf(request, rawHandle, csrf);
        request.Content = JsonContent.Create(body);
        return await client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> SendMalformedJsonAsync(
        string path,
        string rawHandle,
        CsrfPair csrf)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        AddCookiesAndCsrf(request, rawHandle, csrf);
        request.Content = new StringContent("{", Encoding.UTF8, "application/json");
        return await client.SendAsync(request);
    }

    private static void AddCookiesAndCsrf(
        HttpRequestMessage request,
        string rawHandle,
        CsrfPair? csrf)
    {
        var cookies = new List<string> { $"{SessionCookieName}={rawHandle}" };
        if (csrf is not null)
        {
            cookies.Add($"{AntiforgeryCookieName}={csrf.CookieToken}");
        }

        request.Headers.Add(HeaderNames.Cookie, string.Join("; ", cookies));
        if (csrf is not null)
        {
            request.Headers.Add(CsrfHeaderName, csrf.RequestToken);
        }
    }

    private static IReadOnlyList<SetCookieHeaderValue> ParseSetCookies(
        HttpResponseMessage response)
    {
        return response.Headers.TryGetValues(
            HeaderNames.SetCookie,
            out IEnumerable<string>? values)
                ? SetCookieHeaderValue.ParseList(values.ToList()).ToArray()
                : [];
    }

    private static async Task AssertEmptyResponseAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
        Assert.Null(response.Headers.Location);
    }

    private static async Task AssertSafeBadRequestAsync(
        HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        string content = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("System.", content);
        Assert.DoesNotContain("stackTrace", content);
        Assert.DoesNotContain("exceptionType", content);
    }

    private static async Task AssertNoWritesAsync(EnmaDbContext dbContext)
    {
        Assert.Equal(0, await dbContext.ClientPaymentPlans.CountAsync());
        Assert.Equal(0, await dbContext.PaymentInstallments.CountAsync());
        Assert.Equal(0, await dbContext.AuditLogs.CountAsync());
    }

    private static string GetPaymentPlansPath(Guid organizationId)
    {
        return $"/api/organizations/{organizationId:D}/finance/payment-plans";
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed record CsrfResponse(string RequestToken);

    private sealed record CsrfPair(string RequestToken, string CookieToken);
}
