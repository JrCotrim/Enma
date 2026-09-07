using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Enma.Api.Contracts.Finance;
using Enma.Application.Authentication;
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
    public async Task Read_Anonymous_ReturnsUnauthorized()
    {
        Guid organizationId = Guid.NewGuid();

        using HttpResponseMessage list = await client.GetAsync(
            GetPaymentPlansPath(organizationId));
        using HttpResponseMessage detail = await client.GetAsync(
            $"{GetPaymentPlansPath(organizationId)}/{Guid.NewGuid():D}");

        await AssertEmptyResponseAsync(list, HttpStatusCode.Unauthorized);
        await AssertEmptyResponseAsync(detail, HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData(OrganizationRole.Owner, HttpStatusCode.OK)]
    [InlineData(OrganizationRole.Administrator, HttpStatusCode.OK)]
    [InlineData(OrganizationRole.Member, HttpStatusCode.Forbidden)]
    public async Task Read_CurrentFinanceRole_EnforcesListAndDetail(
        OrganizationRole role,
        HttpStatusCode expectedStatus)
    {
        User user = CreateUser($"read-{role}");
        Organization organization = CreateOrganization($"Read {role}");
        OrganizationMembership membership = CreateMembership(user, organization, role);
        var relatedClient = new Client(
            organization.Id, $"{role} Read Client", Now.AddDays(-100));
        var plan = new ClientPaymentPlan(
            organization.Id,
            relatedClient.Id,
            40m,
            4,
            new DateOnly(2026, 7, 7),
            Now.AddDays(-100));
        DateTimeOffset paidAt = Now.AddDays(-50);
        plan.Installments.Single(item => item.SequenceNumber == 2).MarkPaid(paidAt);
        string rawHandle = await SeedAuthenticatedUserAsync(
            user, [organization], [membership], [relatedClient]);
        await SeedFinanceAsync(plan);

        using HttpResponseMessage list = await SendReadAsync(
            GetPaymentPlansPath(organization.Id), rawHandle);
        using HttpResponseMessage detail = await SendReadAsync(
            $"{GetPaymentPlansPath(organization.Id)}/{plan.Id:D}", rawHandle);

        Assert.Equal(expectedStatus, list.StatusCode);
        Assert.Equal(expectedStatus, detail.StatusCode);
        Assert.True(list.Headers.CacheControl?.NoStore);
        Assert.True(detail.Headers.CacheControl?.NoStore);

        if (expectedStatus == HttpStatusCode.OK)
        {
            ListPaymentPlansResponse? listBody = await list.Content
                .ReadFromJsonAsync<ListPaymentPlansResponse>();
            Assert.NotNull(listBody);
            PaymentPlanSummaryResponse summary = Assert.Single(listBody.Items);
            Assert.Equal(plan.Id, summary.Id);
            Assert.Equal(1, listBody.PageNumber);
            Assert.Equal(20, listBody.PageSize);
            Assert.Equal(30m, summary.OutstandingAmount);
            Assert.Equal(1, summary.OverdueInstallmentCount);
            Assert.Equal(new DateOnly(2026, 7, 7), summary.NextDueDate);
            Assert.False(listBody.HasNext);

            PaymentPlanResponse? detailBody = await detail.Content
                .ReadFromJsonAsync<PaymentPlanResponse>();
            Assert.NotNull(detailBody);
            Assert.Equal(relatedClient.Name, detailBody.ClientName);
            Assert.Equal(new DateOnly(2026, 9, 7), detailBody.ReferenceDate);
            Assert.Equal([1, 2, 3, 4],
                detailBody.Installments.Select(item => item.SequenceNumber));
            Assert.Equal(
                [
                    PaymentInstallmentStatusResponse.Overdue,
                    PaymentInstallmentStatusResponse.Paid,
                    PaymentInstallmentStatusResponse.DueToday,
                    PaymentInstallmentStatusResponse.Upcoming
                ],
                detailBody.Installments.Select(item => item.Status));
            Assert.Equal(paidAt, detailBody.Installments[1].PaidAt);
            Assert.Contains("\"status\":\"Overdue\"",
                await detail.Content.ReadAsStringAsync());
        }
        else
        {
            await AssertEmptyResponseAsync(list, HttpStatusCode.Forbidden);
            await AssertEmptyResponseAsync(detail, HttpStatusCode.Forbidden);
        }

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        Assert.Equal(0, await dbContext.AuditLogs.CountAsync());
        Assert.Equal(paidAt, await dbContext.PaymentInstallments
            .Where(item => item.Id == plan.Installments[1].Id)
            .Select(item => item.PaidAt)
            .SingleAsync());
        Assert.Equal(relatedClient.Name, await dbContext.Clients
            .Where(item => item.Id == relatedClient.Id)
            .Select(item => item.Name)
            .SingleAsync());
    }

    [Fact]
    public async Task List_PagingFilterAndValidation_ReturnExpectedContracts()
    {
        User user = CreateUser("read-list");
        Organization organization = CreateOrganization("Read List");
        Organization foreignOrganization = CreateOrganization("Read List Foreign");
        OrganizationMembership membership = CreateMembership(
            user, organization, OrganizationRole.Owner);
        var clientA = new Client(organization.Id, "Client A", Now.AddDays(-3));
        var clientB = new Client(organization.Id, "Client B", Now.AddDays(-3));
        var foreignClient = new Client(
            foreignOrganization.Id, "Foreign", Now.AddDays(-3));
        var first = new ClientPaymentPlan(
            organization.Id, clientA.Id, 10m, 1, new DateOnly(2026, 9, 7), Now.AddDays(-2));
        var second = new ClientPaymentPlan(
            organization.Id, clientB.Id, 20m, 1, new DateOnly(2026, 9, 8), Now.AddDays(-1));
        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            [organization, foreignOrganization],
            [membership],
            [clientA, clientB, foreignClient]);
        await SeedFinanceAsync(first, second);

        using HttpResponseMessage page = await SendReadAsync(
            $"{GetPaymentPlansPath(organization.Id)}?pageNumber=1&pageSize=1",
            rawHandle);
        using HttpResponseMessage filtered = await SendReadAsync(
            $"{GetPaymentPlansPath(organization.Id)}?clientId={clientA.Id:D}",
            rawHandle);
        using HttpResponseMessage foreignFiltered = await SendReadAsync(
            $"{GetPaymentPlansPath(organization.Id)}?clientId={foreignClient.Id:D}",
            rawHandle);
        using HttpResponseMessage invalidPage = await SendReadAsync(
            $"{GetPaymentPlansPath(organization.Id)}?pageSize=101",
            rawHandle);
        using HttpResponseMessage emptyClient = await SendReadAsync(
            $"{GetPaymentPlansPath(organization.Id)}?clientId={Guid.Empty:D}",
            rawHandle);

        ListPaymentPlansResponse? pageBody = await page.Content
            .ReadFromJsonAsync<ListPaymentPlansResponse>();
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.NotNull(pageBody);
        Assert.Equal(second.Id, Assert.Single(pageBody.Items).Id);
        Assert.True(pageBody.HasNext);
        Assert.Equal(clientA.Id, Assert.Single((await filtered.Content
            .ReadFromJsonAsync<ListPaymentPlansResponse>())!.Items).ClientId);
        Assert.Empty((await foreignFiltered.Content
            .ReadFromJsonAsync<ListPaymentPlansResponse>())!.Items);
        await AssertSafeBadRequestAsync(invalidPage);
        await AssertSafeBadRequestAsync(emptyClient);
    }

    [Fact]
    public async Task Detail_MissingAndForeignPlans_ReturnSameNotFound()
    {
        User user = CreateUser("read-detail-missing");
        Organization tenant = CreateOrganization("Read Detail Tenant");
        Organization foreignTenant = CreateOrganization("Read Detail Foreign");
        OrganizationMembership membership = CreateMembership(
            user, tenant, OrganizationRole.Owner);
        var tenantClient = new Client(tenant.Id, "Tenant Client", Now.AddDays(-2));
        var foreignClient = new Client(foreignTenant.Id, "Foreign Client", Now.AddDays(-2));
        var foreignPlan = new ClientPaymentPlan(
            foreignTenant.Id, foreignClient.Id, 10m, 1,
            new DateOnly(2026, 9, 7), Now.AddDays(-1));
        string rawHandle = await SeedAuthenticatedUserAsync(
            user, [tenant, foreignTenant], [membership], [tenantClient, foreignClient]);
        await SeedFinanceAsync(foreignPlan);

        using HttpResponseMessage missing = await SendReadAsync(
            $"{GetPaymentPlansPath(tenant.Id)}/{Guid.NewGuid():D}", rawHandle);
        using HttpResponseMessage foreign = await SendReadAsync(
            $"{GetPaymentPlansPath(tenant.Id)}/{foreignPlan.Id:D}", rawHandle);

        await AssertEmptyResponseAsync(missing, HttpStatusCode.NotFound);
        await AssertEmptyResponseAsync(foreign, HttpStatusCode.NotFound);
        Assert.Equal(
            await missing.Content.ReadAsStringAsync(),
            await foreign.Content.ReadAsStringAsync());
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

    [Fact]
    public async Task MarkPaid_Anonymous_ReturnsUnauthorizedBeforeAntiforgery()
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            GetMarkPaidPath(
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid()));

        using HttpResponseMessage response = await client.SendAsync(request);

        await AssertEmptyResponseAsync(
            response,
            HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData(OrganizationRole.Owner, HttpStatusCode.NoContent)]
    [InlineData(OrganizationRole.Administrator, HttpStatusCode.NoContent)]
    [InlineData(OrganizationRole.Member, HttpStatusCode.Forbidden)]
    public async Task MarkPaid_CurrentFinanceRole_EnforcesAction(
        OrganizationRole role,
        HttpStatusCode expectedStatus)
    {
        User user = CreateUser($"mark-paid-{role}");
        Organization organization = CreateOrganization(
            $"Mark Paid {role}");
        OrganizationMembership membership = CreateMembership(
            user,
            organization,
            role);

        var relatedClient = new Enma.Domain.Clients.Client(
            organization.Id,
            $"Mark Paid Client {role}",
            Now.AddHours(-1));

        var paymentPlan = new Enma.Domain.Finance.ClientPaymentPlan(
            organization.Id,
            relatedClient.Id,
            100m,
            1,
            DateOnly.FromDateTime(Now.UtcDateTime).AddDays(10),
            Now.AddMinutes(-30));

        var installment = Assert.Single(paymentPlan.Installments);

        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            [organization],
            [membership],
            [relatedClient]);

        await SeedFinanceAsync(paymentPlan);

        CsrfPair csrf = await GetCsrfPairAsync(rawHandle);

        using HttpResponseMessage response =
            await SendBodylessMutationAsync(
                GetMarkPaidPath(
                    organization.Id,
                    paymentPlan.Id,
                    installment.Id),
                rawHandle,
                csrf);

        await AssertEmptyResponseAsync(
            response,
            expectedStatus);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();

        var persisted = await dbContext.PaymentInstallments
            .AsNoTracking()
            .SingleAsync(candidate =>
                candidate.Id == installment.Id);

        if (expectedStatus == HttpStatusCode.NoContent)
        {
            Assert.Equal(Now, persisted.PaidAt);

            var auditLog = await dbContext.AuditLogs
                .AsNoTracking()
                .SingleAsync();

            Assert.Equal(
                organization.Id,
                auditLog.OrganizationId);
            Assert.Equal(
                user.Id,
                auditLog.ActorUserId);
            Assert.Equal(
                membership.Id,
                auditLog.ActorMembershipId);
            Assert.Equal(
                role,
                auditLog.ActorRoleAtOccurrence);
            Assert.Equal(
                Enma.Domain.Auditing.AuditEventType.PaymentInstallmentPaid,
                auditLog.EventType);
            Assert.Equal(
                Enma.Domain.Auditing.AuditEntityType.PaymentInstallment,
                auditLog.EntityType);
            Assert.Equal(
                installment.Id,
                auditLog.EntityId);
            Assert.Equal(
                Now,
                auditLog.OccurredAt);
            Assert.Null(auditLog.Details);
        }
        else
        {
            Assert.Null(persisted.PaidAt);
            Assert.Equal(
                0,
                await dbContext.AuditLogs.CountAsync());
        }
    }

    [Fact]
    public async Task MarkPaid_MissingAntiforgery_ReturnsBadRequestWithoutMutation()
    {
        User user = CreateUser("mark-paid-csrf");
        Organization organization = CreateOrganization(
            "Mark Paid Csrf");
        OrganizationMembership membership = CreateMembership(
            user,
            organization,
            OrganizationRole.Owner);

        var relatedClient = new Enma.Domain.Clients.Client(
            organization.Id,
            "Mark Paid Csrf Client",
            Now.AddHours(-1));

        var paymentPlan = new Enma.Domain.Finance.ClientPaymentPlan(
            organization.Id,
            relatedClient.Id,
            100m,
            1,
            DateOnly.FromDateTime(Now.UtcDateTime).AddDays(10),
            Now.AddMinutes(-30));

        var installment = Assert.Single(paymentPlan.Installments);

        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            [organization],
            [membership],
            [relatedClient]);

        await SeedFinanceAsync(paymentPlan);

        using HttpResponseMessage response =
            await SendBodylessMutationAsync(
                GetMarkPaidPath(
                    organization.Id,
                    paymentPlan.Id,
                    installment.Id),
                rawHandle,
                csrf: null);

        await AssertEmptyResponseAsync(
            response,
            HttpStatusCode.BadRequest);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();

        var persisted = await dbContext.PaymentInstallments
            .AsNoTracking()
            .SingleAsync(candidate =>
                candidate.Id == installment.Id);

        Assert.Null(persisted.PaidAt);
        Assert.Equal(
            0,
            await dbContext.AuditLogs.CountAsync());
    }
    [Fact]
    public async Task MarkPaid_UnavailableResources_ReturnNotFoundWithoutMutation()
    {
        User user = CreateUser("mark-paid-not-found");

        Organization organization = CreateOrganization(
            "Mark Paid Available Organization");

        Organization foreignOrganization = CreateOrganization(
            "Mark Paid Foreign Organization");

        OrganizationMembership membership = CreateMembership(
            user,
            organization,
            OrganizationRole.Owner);

        var clientA = new Enma.Domain.Clients.Client(
            organization.Id,
            "Mark Paid Client A",
            Now.AddHours(-1));

        var clientB = new Enma.Domain.Clients.Client(
            organization.Id,
            "Mark Paid Client B",
            Now.AddHours(-1));

        var foreignClient = new Enma.Domain.Clients.Client(
            foreignOrganization.Id,
            "Mark Paid Foreign Client",
            Now.AddHours(-1));

        var planA = new Enma.Domain.Finance.ClientPaymentPlan(
            organization.Id,
            clientA.Id,
            100m,
            1,
            DateOnly.FromDateTime(Now.UtcDateTime).AddDays(10),
            Now.AddMinutes(-30));

        var planB = new Enma.Domain.Finance.ClientPaymentPlan(
            organization.Id,
            clientB.Id,
            100m,
            1,
            DateOnly.FromDateTime(Now.UtcDateTime).AddDays(11),
            Now.AddMinutes(-30));

        var foreignPlan = new Enma.Domain.Finance.ClientPaymentPlan(
            foreignOrganization.Id,
            foreignClient.Id,
            100m,
            1,
            DateOnly.FromDateTime(Now.UtcDateTime).AddDays(12),
            Now.AddMinutes(-30));

        var installmentA = Assert.Single(planA.Installments);
        var installmentB = Assert.Single(planB.Installments);
        var foreignInstallment = Assert.Single(foreignPlan.Installments);

        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            [organization, foreignOrganization],
            [membership],
            [clientA, clientB, foreignClient]);

        await SeedFinanceAsync(
            planA,
            planB,
            foreignPlan);

        CsrfPair csrf = await GetCsrfPairAsync(rawHandle);

        using HttpResponseMessage missingPlan =
            await SendBodylessMutationAsync(
                GetMarkPaidPath(
                    organization.Id,
                    Guid.NewGuid(),
                    Guid.NewGuid()),
                rawHandle,
                csrf);

        using HttpResponseMessage foreignPlanResponse =
            await SendBodylessMutationAsync(
                GetMarkPaidPath(
                    organization.Id,
                    foreignPlan.Id,
                    foreignInstallment.Id),
                rawHandle,
                csrf);

        using HttpResponseMessage missingInstallment =
            await SendBodylessMutationAsync(
                GetMarkPaidPath(
                    organization.Id,
                    planA.Id,
                    Guid.NewGuid()),
                rawHandle,
                csrf);

        using HttpResponseMessage installmentOutsidePlan =
            await SendBodylessMutationAsync(
                GetMarkPaidPath(
                    organization.Id,
                    planA.Id,
                    installmentB.Id),
                rawHandle,
                csrf);

        using HttpResponseMessage foreignInstallmentResponse =
            await SendBodylessMutationAsync(
                GetMarkPaidPath(
                    organization.Id,
                    planA.Id,
                    foreignInstallment.Id),
                rawHandle,
                csrf);

        await AssertEmptyResponseAsync(
            missingPlan,
            HttpStatusCode.NotFound);

        await AssertEmptyResponseAsync(
            foreignPlanResponse,
            HttpStatusCode.NotFound);

        await AssertEmptyResponseAsync(
            missingInstallment,
            HttpStatusCode.NotFound);

        await AssertEmptyResponseAsync(
            installmentOutsidePlan,
            HttpStatusCode.NotFound);

        await AssertEmptyResponseAsync(
            foreignInstallmentResponse,
            HttpStatusCode.NotFound);

        string canonicalNotFoundBody =
            await missingPlan.Content.ReadAsStringAsync();

        Assert.Equal(
            canonicalNotFoundBody,
            await foreignPlanResponse.Content.ReadAsStringAsync());

        Assert.Equal(
            canonicalNotFoundBody,
            await missingInstallment.Content.ReadAsStringAsync());

        Assert.Equal(
            canonicalNotFoundBody,
            await installmentOutsidePlan.Content.ReadAsStringAsync());

        Assert.Equal(
            canonicalNotFoundBody,
            await foreignInstallmentResponse.Content.ReadAsStringAsync());

        await using EnmaDbContext dbContext = fixture.CreateDbContext();

        var persistedA = await dbContext.PaymentInstallments
            .AsNoTracking()
            .SingleAsync(candidate =>
                candidate.Id == installmentA.Id);

        var persistedB = await dbContext.PaymentInstallments
            .AsNoTracking()
            .SingleAsync(candidate =>
                candidate.Id == installmentB.Id);

        var persistedForeign = await dbContext.PaymentInstallments
            .AsNoTracking()
            .SingleAsync(candidate =>
                candidate.Id == foreignInstallment.Id);

        Assert.Null(persistedA.PaidAt);
        Assert.Null(persistedB.PaidAt);
        Assert.Null(persistedForeign.PaidAt);

        Assert.Equal(
            0,
            await dbContext.AuditLogs.CountAsync());
    }

    [Fact]
    public async Task MarkPaid_RepeatedRequest_RemainsNoContentAndEmitsSingleAudit()
    {
        User user = CreateUser("mark-paid-repeat");

        Organization organization = CreateOrganization(
            "Mark Paid Repeat");

        OrganizationMembership membership = CreateMembership(
            user,
            organization,
            OrganizationRole.Owner);

        var relatedClient = new Enma.Domain.Clients.Client(
            organization.Id,
            "Mark Paid Repeat Client",
            Now.AddHours(-1));

        var paymentPlan = new Enma.Domain.Finance.ClientPaymentPlan(
            organization.Id,
            relatedClient.Id,
            100m,
            1,
            DateOnly.FromDateTime(Now.UtcDateTime).AddDays(10),
            Now.AddMinutes(-30));

        var installment = Assert.Single(paymentPlan.Installments);

        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            [organization],
            [membership],
            [relatedClient]);

        await SeedFinanceAsync(paymentPlan);

        CsrfPair csrf = await GetCsrfPairAsync(rawHandle);

        string path = GetMarkPaidPath(
            organization.Id,
            paymentPlan.Id,
            installment.Id);

        using HttpResponseMessage first =
            await SendBodylessMutationAsync(
                path,
                rawHandle,
                csrf);

        using HttpResponseMessage second =
            await SendBodylessMutationAsync(
                path,
                rawHandle,
                csrf);

        await AssertEmptyResponseAsync(
            first,
            HttpStatusCode.NoContent);

        await AssertEmptyResponseAsync(
            second,
            HttpStatusCode.NoContent);

        await using EnmaDbContext dbContext = fixture.CreateDbContext();

        var persisted = await dbContext.PaymentInstallments
            .AsNoTracking()
            .SingleAsync(candidate =>
                candidate.Id == installment.Id);

        Assert.Equal(Now, persisted.PaidAt);

        var auditLogs = await dbContext.AuditLogs
            .AsNoTracking()
            .ToArrayAsync();

        var auditLog = Assert.Single(auditLogs);

        Assert.Equal(
            Enma.Domain.Auditing.AuditEventType.PaymentInstallmentPaid,
            auditLog.EventType);

        Assert.Equal(
            Enma.Domain.Auditing.AuditEntityType.PaymentInstallment,
            auditLog.EntityType);

        Assert.Equal(
            installment.Id,
            auditLog.EntityId);

        Assert.Equal(
            organization.Id,
            auditLog.OrganizationId);

        Assert.Equal(
            membership.Id,
            auditLog.ActorMembershipId);

        Assert.Equal(
            Now,
            auditLog.OccurredAt);

        Assert.Null(auditLog.Details);
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

    private async Task<HttpResponseMessage> SendBodylessMutationAsync(
        string path,
        string rawHandle,
        CsrfPair? csrf)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        AddCookiesAndCsrf(request, rawHandle, csrf);
        return await client.SendAsync(request);
    }

    private static string GetMarkPaidPath(
        Guid organizationId,
        Guid paymentPlanId,
        Guid installmentId)
    {
        return $"{GetPaymentPlansPath(organizationId)}/" +
            $"{paymentPlanId:D}/installments/{installmentId:D}/mark-paid";
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

    private async Task SeedFinanceAsync(params ClientPaymentPlan[] paymentPlans)
    {
        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.ClientPaymentPlans.AddRange(paymentPlans);
        await dbContext.SaveChangesAsync();
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
