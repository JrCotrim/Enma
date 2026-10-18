using System.Net;
using System.Text.Json;
using Enma.Api.Contracts.Finance;
using Enma.Api.Contracts.Processes;
using Microsoft.Playwright;

namespace Enma.E2ETests.Infrastructure;

public sealed class SeededOwner : IDisposable
{
    private readonly SeededSession session;

    public SeededOwner(
        SeededSession session,
        Guid organizationId,
        string organizationName,
        string ownerName)
    {
        ArgumentNullException.ThrowIfNull(session);
        this.session = session;
        OrganizationId = organizationId;
        OrganizationName = organizationName;
        OwnerName = ownerName;
    }

    public Guid OrganizationId { get; }

    public string OrganizationName { get; }

    public string OwnerName { get; }

    public Task<Guid> CreateIndividualClientAsync(string name)
    {
        return session.CreateAsync(
            $"api/organizations/{OrganizationId:D}/clients",
            new { name, email = (string?)null, phone = (string?)null, cpf = (string?)null });
    }

    public Task<Guid> CreateProcessAsync(Guid clientId, string title)
    {
        return session.CreateAsync(
            $"api/organizations/{OrganizationId:D}/processes",
            new { clientId, title });
    }

    public async Task<Guid> CreatePaymentPlanAsync(
        Guid clientId,
        decimal totalAmount,
        int installmentCount,
        DateOnly firstDueDate)
    {
        string content = await session.SendAsync(
            HttpMethod.Post,
            $"api/organizations/{OrganizationId:D}/finance/payment-plans",
            new { clientId, totalAmount, installmentCount, firstDueDate },
            HttpStatusCode.Created,
            withCsrfToken: true);

        return (JsonSerializer.Deserialize<CreatePaymentPlanResponse>(
                content,
                JsonSerializerOptions.Web)
            ?? throw new InvalidOperationException(
                "The payment plan creation returned an empty body."))
            .PaymentPlanId;
    }

    public Task<PaymentPlanResponse> GetPaymentPlanAsync(Guid paymentPlanId)
    {
        return session.GetFromJsonAsync<PaymentPlanResponse>(
            $"api/organizations/{OrganizationId:D}/finance/payment-plans/{paymentPlanId:D}");
    }

    public async Task<IReadOnlyList<string>> ListProcessTitlesAsync()
    {
        ListLegalProcessesResponse response =
            await session.GetFromJsonAsync<ListLegalProcessesResponse>(
                $"api/organizations/{OrganizationId:D}/processes");

        return [.. response.Items.Select(item => item.Title)];
    }

    public Task InviteAsync(string email, string role)
    {
        return session.SendAsync(
            HttpMethod.Post,
            $"api/organizations/{OrganizationId:D}/invitations",
            new { email, role },
            HttpStatusCode.Created,
            withCsrfToken: true);
    }

    public Task DeactivateMemberAsync(Guid membershipId)
    {
        return session.SendAsync(
            HttpMethod.Post,
            $"api/organizations/{OrganizationId:D}/members/{membershipId:D}/deactivate",
            null,
            HttpStatusCode.NoContent,
            withCsrfToken: true);
    }

    // A general (unclassified) document, uploaded as the UI would.
    public async Task<Guid> UploadDocumentAsync(
        string fileName,
        string contentType,
        byte[] content)
    {
        using var file = new ByteArrayContent(content);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        using var form = new MultipartFormDataContent { { file, "file", fileName } };

        return await session.UploadAsync(
            $"api/organizations/{OrganizationId:D}/documents",
            form);
    }

    public Task<ObservedResponse> ObserveAsync(string path)
    {
        return session.ObserveAsync(path);
    }

    // Reuses the API session in the browser instead of a second UI login.
    public Task AuthenticateAsync(IBrowserContext context)
    {
        return session.AuthenticateAsync(context);
    }

    public void Dispose()
    {
        session.Dispose();
    }
}
