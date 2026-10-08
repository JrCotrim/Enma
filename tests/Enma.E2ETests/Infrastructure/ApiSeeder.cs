using System.Net;
using System.Net.Http.Json;
using Enma.Api.Contracts.Onboarding;
using Enma.Api.Contracts.Organizations;

namespace Enma.E2ETests.Infrastructure;

// Builds journey preconditions through the public API (D5): register, verify
// through the real Mailpit e-mail, and sign in once. The returned session is
// injected into browser contexts so journeys do not repeat UI logins.
public sealed class ApiSeeder
{
    private readonly Uri baseAddress;
    private readonly MailpitClient mailpit;

    public ApiSeeder(Uri baseAddress, MailpitClient mailpit)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        ArgumentNullException.ThrowIfNull(mailpit);
        this.baseAddress = baseAddress;
        this.mailpit = mailpit;
    }

    public async Task<SeededOwner> CreateVerifiedOwnerAsync(string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        SyntheticAccount account = SyntheticAccount.Create(label);
        var session = new SeededSession(baseAddress);

        try
        {
            await session.SendAsync(
                HttpMethod.Post,
                "api/onboarding/register",
                new RegisterOrganizationOwnerRequest
                {
                    OrganizationName = account.OrganizationName,
                    OrganizationSlug = account.OrganizationSlug,
                    OwnerName = account.OwnerName,
                    OwnerEmail = account.Email,
                    Password = account.Password
                },
                HttpStatusCode.Created);

            Uri verificationLink = await mailpit.WaitForLinkAsync(
                account.Email,
                new Uri(baseAddress, "verify-email"));
            await session.SendAsync(
                HttpMethod.Post,
                "api/auth/email-verification/verify",
                new { token = MailpitClient.ReadFragmentValue(verificationLink, "token") },
                HttpStatusCode.NoContent);

            await session.SendAsync(
                HttpMethod.Post,
                "api/auth/login",
                new
                {
                    email = account.Email,
                    password = account.Password,
                    completeGoogleLink = false
                },
                HttpStatusCode.NoContent);

            GetCurrentUserOrganizationsResponse organizations =
                await session.GetFromJsonAsync<GetCurrentUserOrganizationsResponse>(
                    "api/me/organizations");
            CurrentUserOrganizationResponse organization =
                Assert.Single(organizations.Items);

            return new SeededOwner(session, organization.Id, organization.Name);
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }
}
