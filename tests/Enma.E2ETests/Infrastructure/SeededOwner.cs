using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Playwright;

namespace Enma.E2ETests.Infrastructure;

public sealed class SeededOwner : IDisposable
{
    private readonly SeededSession session;

    public SeededOwner(
        SeededSession session,
        Guid organizationId,
        string organizationName)
    {
        ArgumentNullException.ThrowIfNull(session);
        this.session = session;
        OrganizationId = organizationId;
        OrganizationName = organizationName;
    }

    public Guid OrganizationId { get; }

    public string OrganizationName { get; }

    public async Task<Guid> CreateIndividualClientAsync(string name)
    {
        string body = await session.SendAsync(
            HttpMethod.Post,
            $"api/organizations/{OrganizationId:D}/clients",
            new { name, email = (string?)null, phone = (string?)null, cpf = (string?)null },
            HttpStatusCode.Created,
            withCsrfToken: true);

        return Guid.Parse(
            JsonNode.Parse(body)?["id"]?.GetValue<string>()
            ?? throw new InvalidOperationException(
                "The client creation response had no id."));
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
