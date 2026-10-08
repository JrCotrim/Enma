using System.Net;
using Enma.E2ETests.Infrastructure;
using Microsoft.Playwright;
using Xunit.Abstractions;
using static Microsoft.Playwright.Assertions;

namespace Enma.E2ETests.Journeys;

// J9 / D10: from tenant A, another tenant's organization must be
// indistinguishable from an organization that does not exist, and another
// tenant's client inside A's own organization must be indistinguishable from a
// client that does not exist. The UI must not reveal B's names either.
[Collection(E2ECollection.Name)]
public sealed class TenantIsolationJourneyTests
{
    private readonly E2EStack stack;
    private readonly ITestOutputHelper output;

    public TenantIsolationJourneyTests(E2EStack stack, ITestOutputHelper output)
    {
        ArgumentNullException.ThrowIfNull(stack);
        ArgumentNullException.ThrowIfNull(output);
        this.stack = stack;
        this.output = output;
    }

    [Fact]
    public Task ForeignTenantResources_AreIndistinguishableFromMissingOnes()
    {
        return BrowserJourney.RunAsync(stack, async page =>
        {
            using SeededOwner ownerA = await stack.Seeder.CreateVerifiedOwnerAsync("Alfa");
            using SeededOwner ownerB = await stack.Seeder.CreateVerifiedOwnerAsync("Beta");
            string clientBName = $"Cliente Beta E2E {Guid.NewGuid():N}"[..30];
            Guid clientB = await ownerB.CreateIndividualClientAsync(clientBName);

            ObservedResponse foreignOrganization = await ownerA.ObserveAsync(
                $"api/organizations/{ownerB.OrganizationId:D}/clients");
            ObservedResponse missingOrganization = await ownerA.ObserveAsync(
                $"api/organizations/{Guid.NewGuid():D}/clients");
            ObservedResponse foreignClient = await ownerA.ObserveAsync(
                $"api/organizations/{ownerA.OrganizationId:D}/clients/{clientB:D}");
            ObservedResponse missingClient = await ownerA.ObserveAsync(
                $"api/organizations/{ownerA.OrganizationId:D}/clients/{Guid.NewGuid():D}");

            output.WriteLine($"(i)   foreign organization: {foreignOrganization}");
            output.WriteLine($"(ii)  missing organization: {missingOrganization}");
            output.WriteLine($"(iii) foreign client:       {foreignClient}");
            output.WriteLine($"(iv)  missing client:       {missingClient}");

            Assert.Equal(missingOrganization, foreignOrganization);
            Assert.Equal(HttpStatusCode.NotFound, foreignClient.Status);
            Assert.Equal(missingClient, foreignClient);

            await ownerA.AuthenticateAsync(page.Context);
            ILocator body = page.Locator("body");

            await page.GotoAsync($"/organizations/{ownerB.OrganizationId:D}/clients");
            await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Organização indisponível" }))
                .ToBeVisibleAsync();
            await Expect(body).Not.ToContainTextAsync(ownerB.OrganizationName);
            await Expect(body).Not.ToContainTextAsync(clientBName);

            await page.GotoAsync(
                $"/organizations/{ownerA.OrganizationId:D}/clients/{clientB:D}");
            await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Cliente indisponível" }))
                .ToBeVisibleAsync();
            await Expect(body).Not.ToContainTextAsync(ownerB.OrganizationName);
            await Expect(body).Not.ToContainTextAsync(clientBName);
        });
    }
}
