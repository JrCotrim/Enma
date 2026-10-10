using System.Text.RegularExpressions;
using Enma.E2ETests.Infrastructure;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Enma.E2ETests.Journeys;

// J2: the Owner invites a Member through the UI; the invitee opens the real
// invitation e-mail, creates the account, verifies it, signs in and lands in
// the organization. Processes are read-only for a Member: the list loads, no
// mutation control renders, and a write through the Member's own session with
// a valid CSRF token is refused with 403.
[Collection(E2ECollection.Name)]
public sealed class MemberInvitationJourneyTests : IClassFixture<E2EHost>
{
    private readonly E2EHost host;

    public MemberInvitationJourneyTests(E2EHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        this.host = host;
    }

    [Fact]
    public Task InvitedMember_JoinsThroughEmailAndProcessesStayReadOnly()
    {
        return BrowserJourney.RunAsync(host, async page =>
        {
            using SeededOwner owner = await host.Seeder.CreateVerifiedOwnerAsync("Delta");
            string suffix = Guid.NewGuid().ToString("N")[..8];
            Guid clientId = await owner.CreateIndividualClientAsync($"Cliente Delta E2E {suffix}");
            string processTitle = $"Processo Delta E2E {suffix}";
            await owner.CreateProcessAsync(clientId, processTitle);
            SyntheticInvitee invitee = SyntheticInvitee.Create("Delta");
            string organizationPath = $"/organizations/{owner.OrganizationId:D}";

            await owner.AuthenticateAsync(page.Context);
            await page.GotoAsync($"{organizationPath}/invitations");
            await page.GetByLabel("E-mail", new() { Exact = true }).FillAsync(invitee.Email);
            await page.GetByLabel("Papel", new() { Exact = true })
                .SelectOptionAsync(new SelectOptionValue { Label = "Membro" });
            await page.GetByRole(AriaRole.Button, new() { Name = "Enviar convite" }).ClickAsync();
            await Expect(page.GetByRole(AriaRole.Status)
                    .Filter(new() { HasText = "Convite criado." }))
                .ToContainTextAsync("O serviço de entrega aceitou o envio.");

            // From here on the browser belongs to the invitee.
            await page.Context.ClearCookiesAsync();
            Uri invitationLink = await host.Mailpit.WaitForLinkAsync(
                invitee.Email,
                new Uri(host.BaseAddress, "accept-invitation"));
            await page.GotoAsync(invitationLink.AbsoluteUri);
            await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Você recebeu um convite" }))
                .ToBeVisibleAsync();
            await Expect(page.Locator("body")).ToContainTextAsync(owner.OrganizationName);

            await page.GetByRole(AriaRole.Link, new() { Name = "Criar conta", Exact = true })
                .ClickAsync();
            await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Criar conta", Exact = true }))
                .ToBeVisibleAsync();
            await page.GetByLabel("Seu nome").FillAsync(invitee.Name);
            await page.GetByLabel("E-mail", new() { Exact = true }).FillAsync(invitee.Email);
            await page.GetByLabel("Senha", new() { Exact = true }).FillAsync(invitee.Password);
            await page.GetByLabel("Confirmar senha").FillAsync(invitee.Password);
            await page.GetByRole(AriaRole.Button, new() { Name = "Criar conta e continuar" })
                .ClickAsync();
            await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Verifique seu e-mail" }))
                .ToBeVisibleAsync();

            Uri verificationLink = await host.Mailpit.WaitForLinkAsync(
                invitee.Email,
                new Uri(host.BaseAddress, "verify-email"));
            await page.GotoAsync(verificationLink.AbsoluteUri);
            await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "E-mail verificado" }))
                .ToBeVisibleAsync();
            await page.GetByRole(AriaRole.Link, new() { Name = "Entrar e continuar convite" })
                .ClickAsync();

            await page.GetByLabel("E-mail", new() { Exact = true }).FillAsync(invitee.Email);
            await page.GetByLabel("Senha", new() { Exact = true }).FillAsync(invitee.Password);
            await page.GetByRole(AriaRole.Button, new() { Name = "Entrar", Exact = true })
                .ClickAsync();
            await Expect(page).ToHaveURLAsync(
                new Regex($"{Regex.Escape(organizationPath)}$"));
            await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Visão geral" }))
                .ToBeVisibleAsync();

            // (a) the list loads for the Member; (b) no mutation control renders.
            await page.GotoAsync($"{organizationPath}/processes");
            ILocator processLink = page.GetByRole(AriaRole.Link, new() { Name = processTitle });
            await Expect(processLink).ToBeVisibleAsync();
            await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Cadastrar processo" }))
                .ToHaveCountAsync(0);

            await processLink.ClickAsync();
            await Expect(page.GetByRole(AriaRole.Heading, new() { Name = processTitle }))
                .ToBeVisibleAsync();
            foreach (string control in new[]
                { "Editar processo", "Alterar responsável", "Suspender", "Encerrar" })
            {
                await Expect(page.GetByRole(AriaRole.Button, new() { Name = control }))
                    .ToHaveCountAsync(0);
            }

            // (c) the backend refuses the write itself, not only the UI.
            IAPIRequestContext memberApi = page.Context.APIRequest;
            IAPIResponse csrf = await memberApi.GetAsync(
                new Uri(host.BaseAddress, "api/auth/csrf").AbsoluteUri);
            Assert.Equal(200, csrf.Status);
            string requestToken = (await csrf.JsonAsync())?
                .GetProperty("requestToken").GetString()
                ?? throw new InvalidOperationException("The CSRF response was invalid.");

            IAPIResponse write = await memberApi.PostAsync(
                new Uri(
                    host.BaseAddress,
                    $"api/organizations/{owner.OrganizationId:D}/processes").AbsoluteUri,
                new APIRequestContextOptions
                {
                    Headers = new Dictionary<string, string>
                    {
                        ["X-CSRF-TOKEN"] = requestToken
                    },
                    DataObject = new { clientId, title = $"Processo indevido E2E {suffix}" }
                });
            Assert.Equal(403, write.Status);
            Assert.Equal([processTitle], await owner.ListProcessTitlesAsync());
        });
    }
}
