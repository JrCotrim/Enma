using Enma.E2ETests.Infrastructure;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Enma.E2ETests.Journeys;

// J10: the login page and the dashboard fit a 375×812 mobile viewport without
// horizontal overflow.
[Collection(E2ECollection.Name)]
public sealed class MobileLayoutJourneyTests : IClassFixture<E2EHost>
{
    private readonly E2EHost host;

    public MobileLayoutJourneyTests(E2EHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        this.host = host;
    }

    [Fact]
    public Task Login_HasNoHorizontalOverflowOnMobile()
    {
        return BrowserJourney.RunAsync(host, async page =>
        {
            await page.GotoAsync("/login");
            await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Entrar no ENMA" }))
                .ToBeVisibleAsync();

            await AssertNoHorizontalOverflowAsync(page);
        }, CreateMobileContextOptions());
    }

    [Fact]
    public Task Dashboard_HasNoHorizontalOverflowOnMobile()
    {
        return BrowserJourney.RunAsync(host, async page =>
        {
            using SeededOwner owner = await host.Seeder.CreateVerifiedOwnerAsync("Gama");
            await owner.AuthenticateAsync(page.Context);

            await page.GotoAsync($"/organizations/{owner.OrganizationId:D}");
            await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Visão geral" }))
                .ToBeVisibleAsync();
            await Expect(page.GetByLabel("Resumo da organização")).ToBeVisibleAsync();

            await AssertNoHorizontalOverflowAsync(page);
        }, CreateMobileContextOptions());
    }

    private static BrowserNewContextOptions CreateMobileContextOptions()
    {
        return new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 375, Height = 812 },
            DeviceScaleFactor = 2,
            IsMobile = true,
            HasTouch = true
        };
    }

    private static async Task AssertNoHorizontalOverflowAsync(IPage page)
    {
        int[] widths = await page.EvaluateAsync<int[]>(
            "() => [document.documentElement.scrollWidth, document.documentElement.clientWidth]");

        Assert.True(
            widths[0] <= widths[1],
            $"Horizontal overflow at {page.Url}: scrollWidth {widths[0]} > clientWidth {widths[1]}.");
    }
}
