using Enma.E2ETests.Infrastructure;
using Microsoft.Playwright;
using Xunit.Abstractions;
using static Microsoft.Playwright.Assertions;

namespace Enma.E2ETests.Journeys;

// J8: the Owner hands the office over to an active Administrator from Equipe.
// A Member is never offered as a target, the confirmation only completes with
// the exact office name, and the change takes effect at once: the former
// Owner's own session loses the transfer action and the backend refuses it,
// while the new Owner's session gets the Owner controls. The audit log labels
// the transfer and names the former Owner as actor.
[Collection(E2ECollection.Name)]
public sealed class OwnershipTransferJourneyTests : IClassFixture<E2EHost>
{
    private const int RoleColumn = 1;
    private const int ActorColumn = 3;
    private const string TransferAction = "Transferir propriedade";
    private const string RenameAction = "Editar nome da organização";

    private readonly E2EHost host;
    private readonly ITestOutputHelper output;

    public OwnershipTransferJourneyTests(E2EHost host, ITestOutputHelper output)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(output);
        this.host = host;
        this.output = output;
    }

    [Fact]
    public Task Owner_TransfersOwnershipToAdministratorWithImmediateEffect()
    {
        return BrowserJourney.RunAsync(host, async page =>
        {
            using SeededOwner owner = await host.Seeder.CreateVerifiedOwnerAsync("Teta");
            using SeededMember administrator = await host.Seeder.AddActiveMemberAsync(
                owner,
                "Teta",
                "Administrator");
            using SeededMember member = await host.Seeder.AddActiveMemberAsync(
                owner,
                "Teta",
                "Member");
            string organizationPath = $"/organizations/{owner.OrganizationId:D}";

            await owner.AuthenticateAsync(page.Context);
            await page.GotoAsync($"{organizationPath}/team");
            ILocator team = TeamTable(page, owner.OrganizationName);
            ILocator ownerRow = MemberRow(team, owner.OwnerName);
            ILocator administratorRow = MemberRow(team, administrator.Name);
            ILocator memberRow = MemberRow(team, member.Name);

            // (a) only the active Administrator is an eligible target.
            await Expect(memberRow.GetByRole(AriaRole.Button, new() { Name = "Alterar papel" }))
                .ToBeVisibleAsync();
            await Expect(memberRow.GetByRole(AriaRole.Button, new() { Name = TransferAction }))
                .ToHaveCountAsync(0);
            await Expect(team.GetByRole(AriaRole.Button, new() { Name = TransferAction }))
                .ToHaveCountAsync(1);

            await administratorRow.GetByRole(AriaRole.Button, new() { Name = TransferAction })
                .ClickAsync();
            ILocator confirmation = administratorRow.GetByRole(
                AriaRole.Alertdialog,
                new() { Name = $"Transferir a propriedade para {administrator.Name}?" });
            ILocator officeName = confirmation.GetByLabel(
                "Digite o nome do escritório para confirmar");
            ILocator confirm = confirmation.GetByRole(
                AriaRole.Button,
                new() { Name = TransferAction });

            // (b) a wrong office name cannot complete the transfer.
            await officeName.FillAsync($"{owner.OrganizationName} errado");
            await Expect(confirm).ToBeDisabledAsync();
            await officeName.FillAsync(owner.OrganizationName[..^1]);
            await Expect(confirm).ToBeDisabledAsync();

            // (c) the exact name completes it.
            await officeName.FillAsync(owner.OrganizationName);
            await Expect(confirm).ToBeEnabledAsync();
            await confirm.ClickAsync();
            await Expect(page.GetByRole(AriaRole.Status)
                    .Filter(new() { HasText = $"Propriedade transferida para {administrator.Name}." }))
                .ToContainTextAsync("Você agora é Administrador.");

            // (d) the roles are swapped in Equipe.
            await Expect(administratorRow.GetByRole(AriaRole.Cell).Nth(RoleColumn))
                .ToHaveTextAsync("Proprietário");
            await Expect(ownerRow.GetByRole(AriaRole.Cell).Nth(RoleColumn))
                .ToHaveTextAsync("Administrador");
            await Expect(memberRow.GetByRole(AriaRole.Cell).Nth(RoleColumn))
                .ToHaveTextAsync("Membro");

            // (e) same session, now an Administrator: once the refreshed role
            // shows (an Administrator may deactivate a Member), no Owner action
            // is left, also after a reload.
            await Expect(memberRow.GetByRole(AriaRole.Button, new() { Name = "Desativar" }))
                .ToBeVisibleAsync();
            await AssertNoOwnerControlsAsync(page, team);
            await page.ReloadAsync();
            await Expect(memberRow.GetByRole(AriaRole.Button, new() { Name = "Desativar" }))
                .ToBeVisibleAsync();
            await AssertNoOwnerControlsAsync(page, team);

            // ...and the backend refuses a transfer back from that session
            // with a valid CSRF token.
            ObservedResponse refused = await TransferThroughApiAsync(
                page.Context.APIRequest,
                owner.OrganizationId,
                administrator.MembershipId);
            output.WriteLine($"(e) former Owner transfer through the API: {refused}");
            Assert.Equal(System.Net.HttpStatusCode.Forbidden, refused.Status);

            // (f) the new Owner, in a browser context of their own, gets the
            // Owner controls.
            await using IBrowserContext newOwnerContext =
                await BrowserJourney.NewContextAsync(host);
            await administrator.AuthenticateAsync(newOwnerContext);
            IPage newOwnerPage = await newOwnerContext.NewPageAsync();
            await newOwnerPage.GotoAsync($"{organizationPath}/team");
            ILocator newOwnerTeam = TeamTable(newOwnerPage, owner.OrganizationName);
            await Expect(MemberRow(newOwnerTeam, administrator.Name).GetByRole(AriaRole.Cell).Nth(RoleColumn))
                .ToHaveTextAsync("Proprietário");
            await Expect(MemberRow(newOwnerTeam, owner.OwnerName)
                    .GetByRole(AriaRole.Button, new() { Name = TransferAction }))
                .ToBeVisibleAsync();
            await Expect(MemberRow(newOwnerTeam, member.Name)
                    .GetByRole(AriaRole.Button, new() { Name = "Alterar papel" }))
                .ToBeVisibleAsync();
            await Expect(newOwnerPage.GetByRole(AriaRole.Button, new() { Name = RenameAction }))
                .ToBeVisibleAsync();

            // (g) the audit log labels the transfer and names the former Owner.
            await newOwnerPage.GotoAsync($"{organizationPath}/audit-log");
            await Expect(newOwnerPage.GetByRole(AriaRole.Heading, new() { Name = "Auditoria" }))
                .ToBeVisibleAsync();
            ILocator transferRow = newOwnerPage.GetByRole(AriaRole.Table)
                .GetByRole(AriaRole.Row)
                .Filter(new() { HasTextString = "Propriedade do escritório transferida" });
            await Expect(transferRow).ToHaveCountAsync(1);
            await Expect(transferRow.GetByRole(AriaRole.Cell).First)
                .ToHaveTextAsync("Propriedade do escritório transferida");
            await Expect(transferRow.GetByRole(AriaRole.Cell).Nth(ActorColumn))
                .ToContainTextAsync(owner.OwnerName);
            await Expect(transferRow).ToContainTextAsync(
                administrator.MembershipId.ToString("D"));
            await Expect(transferRow).Not.ToContainTextAsync("desconhecid");
        });
    }

    private static ILocator TeamTable(IPage page, string organizationName)
    {
        return page.GetByRole(
            AriaRole.Table,
            new() { Name = $"Integrantes da organização {organizationName}" });
    }

    private static ILocator MemberRow(ILocator team, string name)
    {
        return team.GetByRole(AriaRole.Row).Filter(new() { HasTextString = name });
    }

    private static async Task AssertNoOwnerControlsAsync(IPage page, ILocator team)
    {
        await Expect(team.GetByRole(AriaRole.Button, new() { Name = TransferAction }))
            .ToHaveCountAsync(0);
        await Expect(team.GetByRole(AriaRole.Button, new() { Name = "Alterar papel" }))
            .ToHaveCountAsync(0);
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = RenameAction }))
            .ToHaveCountAsync(0);
    }

    private async Task<ObservedResponse> TransferThroughApiAsync(
        IAPIRequestContext api,
        Guid organizationId,
        Guid targetMembershipId)
    {
        IAPIResponse csrf = await api.GetAsync(
            new Uri(host.BaseAddress, "api/auth/csrf").AbsoluteUri);
        Assert.Equal(200, csrf.Status);
        string requestToken = (await csrf.JsonAsync())?
            .GetProperty("requestToken").GetString()
            ?? throw new InvalidOperationException("The CSRF response was invalid.");

        IAPIResponse response = await api.PostAsync(
            new Uri(
                host.BaseAddress,
                $"api/organizations/{organizationId:D}/members/" +
                $"{targetMembershipId:D}/transfer-ownership").AbsoluteUri,
            new APIRequestContextOptions
            {
                Headers = new Dictionary<string, string>
                {
                    ["X-CSRF-TOKEN"] = requestToken
                },
                DataObject = new { expectedTargetRole = "administrator" }
            });

        response.Headers.TryGetValue("content-type", out string? contentType);
        return ObservedResponse.From(
            (System.Net.HttpStatusCode)response.Status,
            contentType?.Split(';')[0].Trim(),
            await response.TextAsync());
    }
}
