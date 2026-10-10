using System.Text.RegularExpressions;
using Enma.Api.Contracts.Finance;
using Enma.E2ETests.Infrastructure;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Enma.E2ETests.Journeys;

// J6: the Owner marks an installment as paid, then undoes the payment with a
// reason from the fixed list. The installment is open again with no payment
// date, and the audit log labels the reversal with its reason and actor.
[Collection(E2ECollection.Name)]
public sealed class PaymentReversalJourneyTests : IClassFixture<E2EHost>
{
    private const int StatusColumn = 3;
    private const int PaymentColumn = 4;
    private const int ActorColumn = 3;
    private const string ReversalReason = "Parcela errada";

    private readonly E2EHost host;

    public PaymentReversalJourneyTests(E2EHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        this.host = host;
    }

    [Fact]
    public Task Owner_MarksInstallmentPaidAndUndoesThePayment()
    {
        return BrowserJourney.RunAsync(host, async page =>
        {
            using SeededOwner owner = await host.Seeder.CreateVerifiedOwnerAsync("Eta");
            string clientName = $"Cliente Eta E2E {Guid.NewGuid():N}"[..28];
            Guid clientId = await owner.CreateIndividualClientAsync(clientName);
            // Due well ahead, so the open installment reads "A vencer" in any
            // time zone.
            Guid paymentPlanId = await owner.CreatePaymentPlanAsync(
                clientId,
                totalAmount: 1500.00m,
                installmentCount: 1,
                firstDueDate: DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(30)));
            PaymentPlanResponse createdPlan = await owner.GetPaymentPlanAsync(paymentPlanId);
            Guid installmentId = Assert.Single(createdPlan.Installments).Id;
            string organizationPath = $"/organizations/{owner.OrganizationId:D}";

            await owner.AuthenticateAsync(page.Context);
            await page.GotoAsync($"{organizationPath}/finance");
            await page.GetByRole(AriaRole.Row)
                .Filter(new() { HasTextString = clientName })
                .GetByRole(AriaRole.Link, new() { Name = "Ver detalhes" })
                .ClickAsync();
            await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Plano de pagamento" }))
                .ToBeVisibleAsync();

            ILocator installment = page
                .GetByRole(AriaRole.Table, new() { Name = "Parcelas do plano de pagamento" })
                .GetByRole(AriaRole.Row)
                .Filter(new() { Has = page.GetByRole(AriaRole.Cell) });
            await Expect(installment).ToHaveCountAsync(1);
            ILocator cells = installment.GetByRole(AriaRole.Cell);
            await Expect(cells.Nth(StatusColumn)).ToHaveTextAsync("A vencer");
            await Expect(cells.Nth(PaymentColumn)).ToHaveTextAsync("—");

            // Pay.
            await installment.GetByRole(AriaRole.Button, new() { Name = "Marcar como paga" })
                .ClickAsync();
            await installment
                .GetByRole(AriaRole.Alertdialog, new() { Name = "Marcar a parcela 1 como paga?" })
                .GetByRole(AriaRole.Button, new() { Name = "Confirmar pagamento" })
                .ClickAsync();
            await Expect(page.GetByRole(AriaRole.Status)
                    .Filter(new() { HasText = "Parcela marcada como paga." }))
                .ToBeVisibleAsync();
            await Expect(cells.Nth(StatusColumn)).ToHaveTextAsync("Paga");
            await Expect(cells.Nth(PaymentColumn)).ToHaveTextAsync(new Regex(@"\d{2}/\d{2}/\d{4}"));
            PaymentInstallmentResponse paid = Assert.Single(
                (await owner.GetPaymentPlanAsync(paymentPlanId)).Installments);
            Assert.Equal(PaymentInstallmentStatusResponse.Paid, paid.Status);
            Assert.NotNull(paid.PaidAt);

            // Undo the payment with a reason from the fixed list.
            await installment.GetByRole(AriaRole.Button, new() { Name = "Desfazer pagamento" })
                .ClickAsync();
            ILocator reversal = installment.GetByRole(
                AriaRole.Alertdialog,
                new() { Name = "Desfazer o pagamento da parcela 1?" });
            await reversal.GetByLabel("Motivo", new() { Exact = true })
                .SelectOptionAsync(new SelectOptionValue { Label = ReversalReason });
            await reversal.GetByRole(AriaRole.Button, new() { Name = "Desfazer pagamento" })
                .ClickAsync();
            await Expect(page.GetByRole(AriaRole.Status)
                    .Filter(new() { HasText = "Pagamento desfeito." }))
                .ToContainTextAsync("A parcela voltou a ficar em aberto.");

            // Open again, without a payment date, also after a reload.
            await Expect(cells.Nth(StatusColumn)).ToHaveTextAsync("A vencer");
            await Expect(cells.Nth(PaymentColumn)).ToHaveTextAsync("—");
            await Expect(installment.GetByRole(AriaRole.Button, new() { Name = "Marcar como paga" }))
                .ToBeVisibleAsync();
            await page.ReloadAsync();
            await Expect(cells.Nth(StatusColumn)).ToHaveTextAsync("A vencer");
            await Expect(cells.Nth(PaymentColumn)).ToHaveTextAsync("—");
            PaymentInstallmentResponse reopened = Assert.Single(
                (await owner.GetPaymentPlanAsync(paymentPlanId)).Installments);
            Assert.NotEqual(PaymentInstallmentStatusResponse.Paid, reopened.Status);
            Assert.Null(reopened.PaidAt);

            await page.GotoAsync($"{organizationPath}/audit-log");
            await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Auditoria" }))
                .ToBeVisibleAsync();
            ILocator installmentEvents = page.GetByRole(AriaRole.Table)
                .GetByRole(AriaRole.Row)
                .Filter(new() { HasTextString = installmentId.ToString("D") });

            ILocator reversalRow = installmentEvents
                .Filter(new() { HasTextString = "Pagamento de parcela desfeito" });
            await Expect(reversalRow.GetByRole(AriaRole.Cell).First)
                .ToHaveTextAsync("Pagamento de parcela desfeito");
            await Expect(reversalRow.GetByRole(AriaRole.Cell).Nth(ActorColumn))
                .ToContainTextAsync(owner.OwnerName);
            await Expect(reversalRow).ToContainTextAsync(ReversalReason);

            ILocator paymentRow = installmentEvents
                .Filter(new() { HasTextString = "Parcela marcada como paga" });
            await Expect(paymentRow.GetByRole(AriaRole.Cell).Nth(ActorColumn))
                .ToContainTextAsync(owner.OwnerName);
        });
    }
}
