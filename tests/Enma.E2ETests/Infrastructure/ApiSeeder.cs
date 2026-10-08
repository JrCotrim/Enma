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

            await VerifyAndSignInAsync(session, account.Email, account.Password);

            GetCurrentUserOrganizationsResponse organizations =
                await session.GetFromJsonAsync<GetCurrentUserOrganizationsResponse>(
                    "api/me/organizations");
            CurrentUserOrganizationResponse organization =
                Assert.Single(organizations.Items);

            return new SeededOwner(
                session,
                organization.Id,
                organization.Name,
                account.OwnerName);
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    // Invites a synthetic person with the given role ("Member" or
    // "Administrator"), registers them from the invitation e-mail, verifies,
    // signs in and accepts. Costs one invitation and one login.
    public async Task<SeededMember> AddActiveMemberAsync(
        SeededOwner owner,
        string label,
        string role)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentException.ThrowIfNullOrWhiteSpace(role);

        SyntheticInvitee invitee = SyntheticInvitee.Create(label);
        await owner.InviteAsync(invitee.Email, role);
        Uri invitationLink = await mailpit.WaitForLinkAsync(
            invitee.Email,
            new Uri(baseAddress, "accept-invitation"));
        string invitationToken =
            MailpitClient.ReadFragmentValue(invitationLink, "token");

        var session = new SeededSession(baseAddress);

        try
        {
            await session.SendAsync(
                HttpMethod.Post,
                "api/onboarding/register-invited",
                new RegisterInvitedUserRequest
                {
                    InvitationToken = invitationToken,
                    Name = invitee.Name,
                    Email = invitee.Email,
                    Password = invitee.Password
                },
                HttpStatusCode.Created);

            await VerifyAndSignInAsync(session, invitee.Email, invitee.Password);

            await session.SendAsync(
                HttpMethod.Post,
                "api/invitations/accept",
                new { token = invitationToken },
                HttpStatusCode.NoContent,
                withCsrfToken: true);

            GetCurrentUserOrganizationsResponse organizations =
                await session.GetFromJsonAsync<GetCurrentUserOrganizationsResponse>(
                    "api/me/organizations");
            CurrentUserOrganizationResponse organization = Assert.Single(
                organizations.Items,
                item => item.Id == owner.OrganizationId);
            Assert.Equal(role, organization.Role);

            return new SeededMember(
                session,
                organization.Id,
                organization.MembershipId,
                invitee.Name);
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    private async Task VerifyAndSignInAsync(
        SeededSession session,
        string email,
        string password)
    {
        Uri verificationLink = await mailpit.WaitForLinkAsync(
            email,
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
                email,
                password,
                completeGoogleLink = false
            },
            HttpStatusCode.NoContent);
    }
}
