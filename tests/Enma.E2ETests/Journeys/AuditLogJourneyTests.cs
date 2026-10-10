using Enma.E2ETests.Infrastructure;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Enma.E2ETests.Journeys;

// J7: the audit log names each actor by their current name. Events come from
// the Owner and from an Administrator who is deactivated afterwards: Owner rows
// show the Owner's name, the Administrator's rows add "(inativo)", and every
// event, entity and actor has a label (no "desconhecido" fallback).
[Collection(E2ECollection.Name)]
public sealed class AuditLogJourneyTests : IClassFixture<E2EHost>
{
    private const int ActorColumn = 3;

    private readonly E2EHost host;

    public AuditLogJourneyTests(E2EHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        this.host = host;
    }

    [Fact]
    public Task AuditLog_ShowsCurrentActorNamesAndLabelsEveryEvent()
    {
        return BrowserJourney.RunAsync(host, async page =>
        {
            using SeededOwner owner = await host.Seeder.CreateVerifiedOwnerAsync("Zeta");
            string suffix = Guid.NewGuid().ToString("N")[..8];
            Guid ownerClientId = await owner.CreateIndividualClientAsync(
                $"Cliente Zeta Titular E2E {suffix}");

            Guid adminMembershipId;
            string adminName;
            Guid adminClientId;
            using (SeededMember admin = await host.Seeder.AddActiveMemberAsync(
                       owner,
                       "Zeta",
                       "Administrator"))
            {
                adminMembershipId = admin.MembershipId;
                adminName = admin.Name;
                adminClientId = await admin.CreateIndividualClientAsync(
                    $"Cliente Zeta Admin E2E {suffix}");
            }

            await owner.DeactivateMemberAsync(adminMembershipId);

            await owner.AuthenticateAsync(page.Context);
            await page.GotoAsync($"/organizations/{owner.OrganizationId:D}/audit-log");
            await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Auditoria" }))
                .ToBeVisibleAsync();
            ILocator table = page.GetByRole(AriaRole.Table);
            await Expect(table.GetByRole(AriaRole.Columnheader).Nth(ActorColumn))
                .ToHaveTextAsync("Ator");

            ILocator adminClientRow = Row(table, adminClientId);
            await Expect(adminClientRow.GetByRole(AriaRole.Cell).First)
                .ToHaveTextAsync("Cliente cadastrado");
            await Expect(adminClientRow.GetByRole(AriaRole.Cell).Nth(ActorColumn))
                .ToContainTextAsync($"{adminName} (inativo)");

            ILocator ownerClientRow = Row(table, ownerClientId);
            await Expect(ownerClientRow.GetByRole(AriaRole.Cell).First)
                .ToHaveTextAsync("Cliente cadastrado");
            await Expect(ownerClientRow.GetByRole(AriaRole.Cell).Nth(ActorColumn))
                .ToContainTextAsync(owner.OwnerName);

            // Other rows may mention the membership id in their details.
            ILocator deactivationRow = Row(table, adminMembershipId)
                .Filter(new() { HasTextString = "Membro desativado" });
            await Expect(deactivationRow.GetByRole(AriaRole.Cell).First)
                .ToHaveTextAsync("Membro desativado");
            await Expect(deactivationRow.GetByRole(AriaRole.Cell).Nth(ActorColumn))
                .ToContainTextAsync(owner.OwnerName);

            await Expect(table).Not.ToContainTextAsync("desconhecid");

            // Every row was produced by one of the two actors; only the
            // deactivated Administrator is marked inactive.
            ILocator rows = table.GetByRole(AriaRole.Row)
                .Filter(new() { Has = page.GetByRole(AriaRole.Cell) });
            int rowCount = await rows.CountAsync();
            var eventLabels = new HashSet<string>();
            for (int index = 0; index < rowCount; index++)
            {
                ILocator cells = rows.Nth(index).GetByRole(AriaRole.Cell);
                eventLabels.Add((await cells.First.InnerTextAsync()).Trim());
                string actor = await cells.Nth(ActorColumn).InnerTextAsync();

                if (actor.Contains(adminName, StringComparison.Ordinal))
                {
                    Assert.Contains($"{adminName} (inativo)", actor, StringComparison.Ordinal);
                }
                else
                {
                    Assert.Contains(owner.OwnerName, actor, StringComparison.Ordinal);
                    Assert.DoesNotContain("(inativo)", actor, StringComparison.Ordinal);
                }
            }

            Assert.Superset(
                new HashSet<string>
                {
                    "Convite criado",
                    "Convite aceito",
                    "Cliente cadastrado",
                    "Membro desativado"
                },
                eventLabels);
        });
    }

    private static ILocator Row(ILocator table, Guid entityId)
    {
        return table.GetByRole(AriaRole.Row)
            .Filter(new() { HasTextString = entityId.ToString("D") });
    }
}
