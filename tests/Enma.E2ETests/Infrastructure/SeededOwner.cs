using System.Net;
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

    public Task<ObservedResponse> ObserveAsync(string path)
    {
        return session.ObserveAsync(path);
    }

    // Reuses the API session in the browser instead of a second UI login.
    public Task AuthenticateAsync(IBrowserContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        System.Net.Cookie sessionCookie = session.SessionCookie;

        return context.AddCookiesAsync(
        [
            new Microsoft.Playwright.Cookie
            {
                Name = sessionCookie.Name,
                Value = sessionCookie.Value,
                Url = session.BaseAddress.GetLeftPart(UriPartial.Authority) + "/",
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteAttribute.Lax
            }
        ]);
    }

    public void Dispose()
    {
        session.Dispose();
    }
}
