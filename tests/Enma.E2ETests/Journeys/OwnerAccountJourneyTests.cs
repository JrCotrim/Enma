using System.Text.RegularExpressions;
using Enma.E2ETests.Infrastructure;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Enma.E2ETests.Journeys;

// J1: register through the UI, open the real verification e-mail link, sign
// in, reach the dashboard, sign out, and confirm protected routes are closed.
[Collection(E2ECollection.Name)]
public sealed class OwnerAccountJourneyTests : IClassFixture<E2EHost>
{
    private readonly E2EHost host;

    public OwnerAccountJourneyTests(E2EHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        this.host = host;
    }

    [Fact]
    public Task Owner_RegistersVerifiesSignsInAndSignsOut()
    {
        return BrowserJourney.RunAsync(host, async page =>
        {
            SyntheticAccount account = SyntheticAccount.Create("Alfa");

            await page.GotoAsync("/register");
            await page.GetByLabel("Nome da organização")
                .FillAsync(account.OrganizationName);
            await page.GetByLabel("Nome curto da organização")
                .FillAsync(account.OrganizationSlug);
            await page.GetByLabel("Seu nome").FillAsync(account.OwnerName);
            await page.GetByLabel("E-mail", new() { Exact = true })
                .FillAsync(account.Email);
            await page.GetByLabel("Senha", new() { Exact = true })
                .FillAsync(account.Password);
            await page.GetByLabel("Confirmar senha").FillAsync(account.Password);
            await page.GetByRole(AriaRole.Button, new() { Name = "Criar conta", Exact = true })
                .ClickAsync();
            await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Verifique seu e-mail" }))
                .ToBeVisibleAsync();

            Uri verificationLink = await host.Mailpit.WaitForLinkAsync(
                account.Email,
                new Uri(host.BaseAddress, "verify-email"));
            await page.GotoAsync(verificationLink.AbsoluteUri);
            await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "E-mail verificado" }))
                .ToBeVisibleAsync();

            await page.GotoAsync("/login");
            await page.GetByLabel("E-mail", new() { Exact = true })
                .FillAsync(account.Email);
            await page.GetByLabel("Senha", new() { Exact = true })
                .FillAsync(account.Password);
            await page.GetByRole(AriaRole.Button, new() { Name = "Entrar", Exact = true })
                .ClickAsync();
            await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Suas organizações" }))
                .ToBeVisibleAsync();

            await page.GetByRole(AriaRole.Link, new() { Name = $"Acessar {account.OrganizationName}" })
                .ClickAsync();
            await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Visão geral" }))
                .ToBeVisibleAsync();
            string dashboardUrl = page.Url;

            await page.GetByRole(AriaRole.Button, new() { Name = "Perfil" }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Sair" }).ClickAsync();
            await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Entrar no ENMA" }))
                .ToBeVisibleAsync();

            await page.GotoAsync(dashboardUrl);
            await Expect(page).ToHaveURLAsync(new Regex("/login(\\?|$)"));
            await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Entrar no ENMA" }))
                .ToBeVisibleAsync();
        });
    }
}
