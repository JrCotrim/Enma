using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Enma.Api.Contracts.Finance;
using Enma.Application.Authentication;
using Enma.Domain.Auditing;
using Enma.Domain.Authentication;
using Enma.Domain.Clients;
using Enma.Domain.Finance;
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
public sealed class PaymentReversalEndpointTests : IAsyncLifetime
{
    private const string CsrfPath = "/api/auth/csrf";
    private const string SessionCookieName = "__Host-enma_session";
    private const string AntiforgeryCookieName = "__Host-enma_csrf";
    private const string CsrfHeaderName = "X-CSRF-TOKEN";
    private const string PasswordHash =
        "synthetic-payment-reversal-endpoint-password-hash";

    // 2026-10-03T15:00Z is 12:00 on 2026-10-03 in America/Sao_Paulo.
    private static readonly DateTimeOffset Now = new(
        2026, 10, 3, 15, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly OperationalToday = new(2026, 10, 3);
    private static readonly DateTimeOffset PlanCreatedAt = Now.AddDays(-10);
    private static readonly DateTimeOffset PaidAt = Now.AddDays(-2);

    private readonly PostgreSqlFixture fixture;
    private readonly EnmaApiFactory factory;
    private readonly HttpClient client;

    public PaymentReversalEndpointTests(PostgreSqlFixture fixture)
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
    public void Contract_ExposesOnlyReason()
    {
        Assert.Equal(
            [nameof(ReverseInstallmentPaymentRequest.Reason)],
            typeof(ReverseInstallmentPaymentRequest)
                .GetProperties()
                .Select(property => property.Name)
                .ToArray());
    }

    [Fact]
    public async Task Reverse_Anonymous_ReturnsUnauthorizedBeforeAntiforgery()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            GetReversePath(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));
        request.Content = JsonContent.Create(new { reason = "other" });

        using HttpResponseMessage response = await client.SendAsync(request);

        await AssertEmptyResponseAsync(response, HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData(OrganizationRole.Owner, HttpStatusCode.NoContent)]
    [InlineData(OrganizationRole.Administrator, HttpStatusCode.NoContent)]
    [InlineData(OrganizationRole.Member, HttpStatusCode.Forbidden)]
    public async Task Reverse_CurrentFinanceRole_EnforcesAction(
        OrganizationRole role,
        HttpStatusCode expectedStatus)
    {
        SeededTenant tenant = await SeedTenantAsync($"Reverse {role}", role);
        CsrfPair csrf = await GetCsrfPairAsync(tenant.RawHandle);

        using HttpResponseMessage response = await SendReverseAsync(
            GetReversePath(
                tenant.Organization.Id,
                tenant.PaymentPlan.Id,
                tenant.Installment.Id),
            tenant.RawHandle,
            csrf,
            new { reason = "registeredByMistake" });

        await AssertEmptyResponseAsync(response, expectedStatus);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        PaymentInstallment persisted = await GetInstallmentAsync(
            dbContext,
            tenant.Installment.Id);

        if (expectedStatus == HttpStatusCode.NoContent)
        {
            Assert.Null(persisted.PaidAt);

            AuditLog auditLog = await dbContext.AuditLogs
                .AsNoTracking()
                .SingleAsync();
            Assert.Equal(tenant.Organization.Id, auditLog.OrganizationId);
            Assert.Equal(tenant.User.Id, auditLog.ActorUserId);
            Assert.Equal(tenant.Membership.Id, auditLog.ActorMembershipId);
            Assert.Equal(role, auditLog.ActorRoleAtOccurrence);
            Assert.Equal(
                AuditEventType.PaymentInstallmentPaymentReversed,
                auditLog.EventType);
            Assert.Equal(AuditEntityType.PaymentInstallment, auditLog.EntityType);
            Assert.Equal(tenant.Installment.Id, auditLog.EntityId);
            Assert.Equal(Now, auditLog.OccurredAt);
            Assert.Equal(
                PaymentReversalReason.RegisteredByMistake,
                Assert.IsType<PaymentInstallmentPaymentReversedAuditDetails>(
                    auditLog.Details).Reason);
        }
        else
        {
            Assert.Equal(PaidAt, persisted.PaidAt);
            Assert.Equal(0, await dbContext.AuditLogs.CountAsync());
        }
    }

    [Fact]
    public async Task Reverse_MissingAntiforgery_ReturnsBadRequestWithoutMutation()
    {
        SeededTenant tenant = await SeedTenantAsync(
            "Reverse Csrf",
            OrganizationRole.Owner);

        using HttpResponseMessage response = await SendReverseAsync(
            GetReversePath(
                tenant.Organization.Id,
                tenant.PaymentPlan.Id,
                tenant.Installment.Id),
            tenant.RawHandle,
            csrf: null,
            new { reason = "other" });

        await AssertEmptyResponseAsync(response, HttpStatusCode.BadRequest);
        await AssertStillPaidWithoutAuditAsync(tenant.Installment.Id);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"reason":null}""")]
    [InlineData("""{"reason":""}""")]
    [InlineData("""{"reason":"refund"}""")]
    [InlineData("""{"reason":"RegisteredByMistake"}""")]
    [InlineData("""{"reason":"registered_by_mistake"}""")]
    [InlineData("""{"reason":1}""")]
    [InlineData("""{"reason":["other"]}""")]
    [InlineData("{")]
    public async Task Reverse_MissingOrInvalidReason_ReturnsBadRequestWithoutMutation(
        string body)
    {
        SeededTenant tenant = await SeedTenantAsync(
            "Reverse Invalid Reason",
            OrganizationRole.Owner);
        CsrfPair csrf = await GetCsrfPairAsync(tenant.RawHandle);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            GetReversePath(
                tenant.Organization.Id,
                tenant.PaymentPlan.Id,
                tenant.Installment.Id));
        AddCookiesAndCsrf(request, tenant.RawHandle, csrf);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using HttpResponseMessage response = await client.SendAsync(request);

        await AssertSafeBadRequestAsync(response);
        await AssertStillPaidWithoutAuditAsync(tenant.Installment.Id);
    }

    [Fact]
    public async Task Reverse_UnavailableResources_ReturnSameNotFoundWithoutMutation()
    {
        SeededTenant tenant = await SeedTenantAsync(
            "Reverse Not Found",
            OrganizationRole.Owner);
        SeededTenant foreign = await SeedTenantAsync(
            "Reverse Foreign",
            OrganizationRole.Owner);
        ClientPaymentPlan otherPlan = CreatePaidPlan(
            tenant.Organization,
            tenant.Client,
            1);
        PaymentInstallment otherInstallment = Assert.Single(otherPlan.Installments);
        await SeedFinanceAsync(otherPlan);
        CsrfPair csrf = await GetCsrfPairAsync(tenant.RawHandle);

        string[] paths =
        [
            GetReversePath(tenant.Organization.Id, Guid.NewGuid(), Guid.NewGuid()),
            GetReversePath(
                tenant.Organization.Id,
                foreign.PaymentPlan.Id,
                foreign.Installment.Id),
            GetReversePath(
                tenant.Organization.Id,
                tenant.PaymentPlan.Id,
                Guid.NewGuid()),
            GetReversePath(
                tenant.Organization.Id,
                tenant.PaymentPlan.Id,
                otherInstallment.Id),
            GetReversePath(
                tenant.Organization.Id,
                tenant.PaymentPlan.Id,
                foreign.Installment.Id),
            GetReversePath(
                tenant.Organization.Id,
                Guid.Empty,
                tenant.Installment.Id)
        ];

        var bodies = new List<string>();

        foreach (string path in paths)
        {
            using HttpResponseMessage response = await SendReverseAsync(
                path,
                tenant.RawHandle,
                csrf,
                new { reason = "other" });

            await AssertEmptyResponseAsync(response, HttpStatusCode.NotFound);
            bodies.Add(await response.Content.ReadAsStringAsync());
        }

        Assert.Single(bodies.Distinct(StringComparer.Ordinal));

        await AssertStillPaidWithoutAuditAsync(tenant.Installment.Id);
        await AssertStillPaidWithoutAuditAsync(otherInstallment.Id);
        await AssertStillPaidWithoutAuditAsync(foreign.Installment.Id);
    }

    [Fact]
    public async Task Reverse_ForeignOrganizationRoute_IsForbiddenWithoutMutation()
    {
        SeededTenant tenant = await SeedTenantAsync(
            "Reverse Route Owner",
            OrganizationRole.Owner);
        SeededTenant foreign = await SeedTenantAsync(
            "Reverse Route Foreign",
            OrganizationRole.Owner);
        CsrfPair csrf = await GetCsrfPairAsync(tenant.RawHandle);

        using HttpResponseMessage response = await SendReverseAsync(
            GetReversePath(
                foreign.Organization.Id,
                foreign.PaymentPlan.Id,
                foreign.Installment.Id),
            tenant.RawHandle,
            csrf,
            new { reason = "other" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertStillPaidWithoutAuditAsync(foreign.Installment.Id);
    }

    [Fact]
    public async Task Reverse_UnpaidInstallment_ReturnsNoContentWithoutAudit()
    {
        SeededTenant tenant = await SeedTenantAsync(
            "Reverse Unpaid",
            OrganizationRole.Owner,
            paid: false);
        CsrfPair csrf = await GetCsrfPairAsync(tenant.RawHandle);

        using HttpResponseMessage response = await SendReverseAsync(
            GetReversePath(
                tenant.Organization.Id,
                tenant.PaymentPlan.Id,
                tenant.Installment.Id),
            tenant.RawHandle,
            csrf,
            new { reason = "other" });

        await AssertEmptyResponseAsync(response, HttpStatusCode.NoContent);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Null((await GetInstallmentAsync(
            dbContext,
            tenant.Installment.Id)).PaidAt);
        Assert.Equal(0, await dbContext.AuditLogs.CountAsync());
    }

    [Fact]
    public async Task Reverse_RepeatedRequest_RemainsNoContentAndEmitsSingleAudit()
    {
        SeededTenant tenant = await SeedTenantAsync(
            "Reverse Repeat",
            OrganizationRole.Administrator);
        CsrfPair csrf = await GetCsrfPairAsync(tenant.RawHandle);
        string path = GetReversePath(
            tenant.Organization.Id,
            tenant.PaymentPlan.Id,
            tenant.Installment.Id);

        using HttpResponseMessage first = await SendReverseAsync(
            path,
            tenant.RawHandle,
            csrf,
            new { reason = "paymentNotCompleted" });
        using HttpResponseMessage second = await SendReverseAsync(
            path,
            tenant.RawHandle,
            csrf,
            new { reason = "other" });

        await AssertEmptyResponseAsync(first, HttpStatusCode.NoContent);
        await AssertEmptyResponseAsync(second, HttpStatusCode.NoContent);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Null((await GetInstallmentAsync(
            dbContext,
            tenant.Installment.Id)).PaidAt);
        AuditLog auditLog = Assert.Single(
            await dbContext.AuditLogs.AsNoTracking().ToArrayAsync());
        Assert.Equal(
            PaymentReversalReason.PaymentNotCompleted,
            Assert.IsType<PaymentInstallmentPaymentReversedAuditDetails>(
                auditLog.Details).Reason);
    }

    [Fact]
    public async Task Reverse_AppearsInAuditLogWithCodeAndReasonOnly()
    {
        SeededTenant tenant = await SeedTenantAsync(
            "Reverse Audit Read",
            OrganizationRole.Owner);
        CsrfPair csrf = await GetCsrfPairAsync(tenant.RawHandle);

        using HttpResponseMessage mutation = await SendReverseAsync(
            GetReversePath(
                tenant.Organization.Id,
                tenant.PaymentPlan.Id,
                tenant.Installment.Id),
            tenant.RawHandle,
            csrf,
            new { reason = "wrongInstallment" });
        await AssertEmptyResponseAsync(mutation, HttpStatusCode.NoContent);

        using HttpResponseMessage auditResponse = await SendReadAsync(
            $"/api/organizations/{tenant.Organization.Id:D}/audit-logs" +
                "?eventType=payment_installment.payment_reversed",
            tenant.RawHandle);

        Assert.Equal(HttpStatusCode.OK, auditResponse.StatusCode);
        string auditJson = await auditResponse.Content.ReadAsStringAsync();
        using JsonDocument auditDocument = JsonDocument.Parse(auditJson);
        JsonElement item = Assert.Single(auditDocument.RootElement
            .GetProperty("items")
            .EnumerateArray()
            .ToArray());
        Assert.Equal(
            "payment_installment.payment_reversed",
            item.GetProperty("eventType").GetString());
        Assert.Equal(
            "payment_installment",
            item.GetProperty("entityType").GetString());
        Assert.Equal(tenant.Installment.Id, item.GetProperty("entityId").GetGuid());
        Assert.Equal(
            tenant.Membership.Id,
            item.GetProperty("actorMembershipId").GetGuid());
        JsonElement details = item.GetProperty("details");
        Assert.Equal(
            "payment_installment.payment_reversed",
            details.GetProperty("type").GetString());
        Assert.Equal("WrongInstallment", details.GetProperty("reason").GetString());
        Assert.Equal(
            ["type", "reason"],
            details.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.DoesNotContain("100.00", auditJson, StringComparison.Ordinal);
        Assert.DoesNotContain(tenant.Client.Name, auditJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reverse_ReopenedInstallments_AreReflectedByFinanceReads()
    {
        SeededTenant tenant = await SeedTenantAsync(
            "Reverse Reads",
            OrganizationRole.Owner);
        ClientPaymentPlan dueTodayPlan = new(
            tenant.Organization.Id,
            tenant.Client.Id,
            50m,
            1,
            OperationalToday,
            PlanCreatedAt);
        PaymentInstallment dueTodayInstallment =
            Assert.Single(dueTodayPlan.Installments);
        dueTodayInstallment.MarkPaid(PaidAt);
        await SeedFinanceAsync(dueTodayPlan);
        CsrfPair csrf = await GetCsrfPairAsync(tenant.RawHandle);

        FinanceOverviewResponse overviewBefore =
            await ReadAsync<FinanceOverviewResponse>(
                GetFinanceOverviewPath(tenant.Organization.Id),
                tenant.RawHandle);

        Assert.Equal(150m, overviewBefore.TotalReceivedAmount);
        Assert.Equal(200m, overviewBefore.TotalOutstandingAmount);
        Assert.Equal(0m, overviewBefore.OverdueAmount);
        Assert.Equal(0m, overviewBefore.DueTodayAmount);
        Assert.Equal(2L, overviewBefore.PaidInstallmentCount);

        foreach ((Guid planId, Guid installmentId) in new[]
        {
            (tenant.PaymentPlan.Id, tenant.Installment.Id),
            (dueTodayPlan.Id, dueTodayInstallment.Id)
        })
        {
            using HttpResponseMessage response = await SendReverseAsync(
                GetReversePath(tenant.Organization.Id, planId, installmentId),
                tenant.RawHandle,
                csrf,
                new { reason = "paymentNotCompleted" });
            await AssertEmptyResponseAsync(response, HttpStatusCode.NoContent);
        }

        FinanceOverviewResponse overview =
            await ReadAsync<FinanceOverviewResponse>(
                GetFinanceOverviewPath(tenant.Organization.Id),
                tenant.RawHandle);
        ClientFinanceSummaryResponse clientSummary =
            await ReadAsync<ClientFinanceSummaryResponse>(
                GetClientFinanceSummaryPath(
                    tenant.Organization.Id,
                    tenant.Client.Id),
                tenant.RawHandle);
        PaymentPlanResponse overduePlanDetail =
            await ReadAsync<PaymentPlanResponse>(
                $"{GetPaymentPlansPath(tenant.Organization.Id)}/" +
                    $"{tenant.PaymentPlan.Id:D}",
                tenant.RawHandle);
        PaymentPlanResponse dueTodayPlanDetail =
            await ReadAsync<PaymentPlanResponse>(
                $"{GetPaymentPlansPath(tenant.Organization.Id)}/" +
                    $"{dueTodayPlan.Id:D}",
                tenant.RawHandle);

        Assert.Equal(OperationalToday, overview.ReferenceDate);
        Assert.Equal(350m, overview.TotalContractedAmount);
        Assert.Equal(0m, overview.TotalReceivedAmount);
        Assert.Equal(350m, overview.TotalOutstandingAmount);
        Assert.Equal(100m, overview.OverdueAmount);
        Assert.Equal(50m, overview.DueTodayAmount);
        Assert.Equal(200m, overview.UpcomingAmount);
        Assert.Equal(0L, overview.PaidInstallmentCount);
        Assert.Equal(1L, overview.OverdueInstallmentCount);
        Assert.Equal(1L, overview.DueTodayInstallmentCount);
        Assert.Equal(2L, overview.UpcomingInstallmentCount);
        Assert.Equal(2L, overview.OpenPaymentPlanCount);

        Assert.Equal(0m, clientSummary.TotalReceivedAmount);
        Assert.Equal(350m, clientSummary.TotalOutstandingAmount);
        Assert.Equal(100m, clientSummary.OverdueAmount);

        PaymentInstallmentResponse reopenedOverdue = overduePlanDetail.Installments
            .Single(installment => installment.Id == tenant.Installment.Id);
        Assert.Null(reopenedOverdue.PaidAt);
        Assert.Equal(PaymentInstallmentStatusResponse.Overdue, reopenedOverdue.Status);

        PaymentInstallmentResponse reopenedDueToday =
            Assert.Single(dueTodayPlanDetail.Installments);
        Assert.Null(reopenedDueToday.PaidAt);
        Assert.Equal(PaymentInstallmentStatusResponse.DueToday, reopenedDueToday.Status);
    }

    private async Task<SeededTenant> SeedTenantAsync(
        string marker,
        OrganizationRole role,
        bool paid = true)
    {
        var user = new User(
            $"Reversal HTTP {marker}",
            $"reversal-http-{Guid.NewGuid():N}@example.test",
            Now.AddDays(-20));
        user.VerifyEmail(Now.AddDays(-19));
        var organization = new Organization(
            $"{marker} Finance",
            $"reversal-{Guid.NewGuid():N}",
            Now.AddDays(-20));
        var membership = new OrganizationMembership(
            organization.Id,
            user.Id,
            role,
            Now.AddDays(-19));
        var relatedClient = new Client(
            organization.Id,
            $"{marker} Client",
            Now.AddDays(-19));

        // Three monthly installments of 100: yesterday (overdue when open),
        // next month and the month after (upcoming).
        var paymentPlan = new ClientPaymentPlan(
            organization.Id,
            relatedClient.Id,
            300m,
            3,
            OperationalToday.AddDays(-1),
            PlanCreatedAt);
        PaymentInstallment installment = paymentPlan.Installments
            .Single(candidate => candidate.SequenceNumber == 1);

        if (paid)
        {
            installment.MarkPaid(PaidAt);
        }

        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            organization,
            membership,
            relatedClient);
        await SeedFinanceAsync(paymentPlan);

        return new SeededTenant(
            user,
            organization,
            membership,
            relatedClient,
            paymentPlan,
            installment,
            rawHandle);
    }

    private static ClientPaymentPlan CreatePaidPlan(
        Organization organization,
        Client relatedClient,
        int installmentCount)
    {
        var paymentPlan = new ClientPaymentPlan(
            organization.Id,
            relatedClient.Id,
            100m * installmentCount,
            installmentCount,
            OperationalToday.AddDays(5),
            PlanCreatedAt);

        foreach (PaymentInstallment installment in paymentPlan.Installments)
        {
            installment.MarkPaid(PaidAt);
        }

        return paymentPlan;
    }

    private async Task<string> SeedAuthenticatedUserAsync(
        User user,
        Organization organization,
        OrganizationMembership membership,
        Client relatedClient)
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
        dbContext.Organizations.Add(organization);
        dbContext.Users.Add(user);
        dbContext.UserCredentials.Add(credential);
        dbContext.OrganizationMemberships.Add(membership);
        dbContext.Clients.Add(relatedClient);
        dbContext.AuthenticationSessions.Add(session);
        await dbContext.SaveChangesAsync();

        return rawHandle;
    }

    private async Task SeedFinanceAsync(params ClientPaymentPlan[] paymentPlans)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.ClientPaymentPlans.AddRange(paymentPlans);
        await dbContext.SaveChangesAsync();
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

    private async Task<HttpResponseMessage> SendReverseAsync(
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

    private async Task<HttpResponseMessage> SendReadAsync(
        string path,
        string rawHandle)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(
            HeaderNames.Cookie,
            $"{SessionCookieName}={rawHandle}");
        return await client.SendAsync(request);
    }

    private async Task<T> ReadAsync<T>(string path, string rawHandle)
    {
        using HttpResponseMessage response = await SendReadAsync(path, rawHandle);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        T? body = await response.Content.ReadFromJsonAsync<T>();
        Assert.NotNull(body);
        return body;
    }

    private async Task AssertStillPaidWithoutAuditAsync(Guid installmentId)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Equal(PaidAt, (await GetInstallmentAsync(
            dbContext,
            installmentId)).PaidAt);
        Assert.Equal(0, await dbContext.AuditLogs.CountAsync());
    }

    private static Task<PaymentInstallment> GetInstallmentAsync(
        EnmaDbContext dbContext,
        Guid installmentId)
    {
        return dbContext.PaymentInstallments
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == installmentId);
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

    private static string GetReversePath(
        Guid organizationId,
        Guid paymentPlanId,
        Guid installmentId)
    {
        return $"{GetPaymentPlansPath(organizationId)}/" +
            $"{paymentPlanId:D}/installments/{installmentId:D}/reverse-payment";
    }

    private static string GetPaymentPlansPath(Guid organizationId)
    {
        return $"/api/organizations/{organizationId:D}/finance/payment-plans";
    }

    private static string GetFinanceOverviewPath(Guid organizationId)
    {
        return $"/api/organizations/{organizationId:D}/finance/overview";
    }

    private static string GetClientFinanceSummaryPath(
        Guid organizationId,
        Guid clientId)
    {
        return $"/api/organizations/{organizationId:D}/finance/clients/" +
            $"{clientId:D}/summary";
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed record SeededTenant(
        User User,
        Organization Organization,
        OrganizationMembership Membership,
        Client Client,
        ClientPaymentPlan PaymentPlan,
        PaymentInstallment Installment,
        string RawHandle);

    private sealed record CsrfResponse(string RequestToken);

    private sealed record CsrfPair(string RequestToken, string CookieToken);
}
