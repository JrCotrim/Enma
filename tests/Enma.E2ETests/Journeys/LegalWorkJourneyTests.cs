using System.Globalization;
using System.Text.RegularExpressions;
using Enma.E2ETests.Infrastructure;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Enma.E2ETests.Journeys;

// J3: the Owner registers an individual (CPF) and a company (alphanumeric
// CNPJ) client, opens a process for the company with a second member as
// responsible, creates a deadline on that process — the form suggests the
// process responsible — and completes it.
[Collection(E2ECollection.Name)]
public sealed class LegalWorkJourneyTests
{
    // Synthetic check-digit-valid documents, the same ones the integration
    // tests use; neither belongs to a real person or company.
    private const string SyntheticCpf = "529.982.247-25";
    private const string SyntheticCnpj = "12.ABC.345/01DE-35";

    private readonly E2EStack stack;

    public LegalWorkJourneyTests(E2EStack stack)
    {
        ArgumentNullException.ThrowIfNull(stack);
        this.stack = stack;
    }

    [Fact]
    public Task Owner_RegistersClientsProcessAndCompletesDeadline()
    {
        return BrowserJourney.RunAsync(stack, async page =>
        {
            using SeededOwner owner = await stack.Seeder.CreateVerifiedOwnerAsync("Epsilon");
            using SeededMember responsible = await stack.Seeder.AddActiveMemberAsync(
                owner,
                "Epsilon",
                "Member");
            string suffix = Guid.NewGuid().ToString("N")[..8];
            string individualName = $"Cliente PF E2E {suffix}";
            string companyName = $"Empresa PJ E2E {suffix}";
            string processTitle = $"Processo Epsilon E2E {suffix}";
            string deadlineTitle = $"Prazo Epsilon E2E {suffix}";
            string dueDate = DateTime.UtcNow.Date.AddDays(7)
                .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string organizationPath = $"/organizations/{owner.OrganizationId:D}";

            await owner.AuthenticateAsync(page.Context);

            await page.GotoAsync($"{organizationPath}/clients");
            await CreateClientAsync(page, "Pessoa física", "Nome", individualName, "CPF", SyntheticCpf);
            await CreateClientAsync(page, "Pessoa jurídica", "Razão social", companyName, "CNPJ", SyntheticCnpj);
            await page.GetByRole(AriaRole.Link, new() { Name = companyName }).ClickAsync();
            await Expect(page.GetByRole(AriaRole.Heading, new() { Name = companyName }))
                .ToBeVisibleAsync();
            await Expect(page.GetByRole(AriaRole.Definition)
                    .Filter(new() { HasTextString = SyntheticCnpj }))
                .ToBeVisibleAsync();

            await page.GotoAsync($"{organizationPath}/processes");
            await page.GetByRole(AriaRole.Button, new() { Name = "Cadastrar processo" }).ClickAsync();
            ILocator clientSearch = page.GetByLabel("Buscar cliente", new() { Exact = true });
            await clientSearch.FillAsync(companyName);
            await clientSearch.PressAsync("Enter");
            await page.GetByRole(AriaRole.Button, new() { Name = $"Selecionar {companyName}" })
                .ClickAsync();
            await page.GetByLabel("Título", new() { Exact = true }).FillAsync(processTitle);
            await CreateFormResponsible(page).SelectOptionAsync(
                new SelectOptionValue { Label = "Outra pessoa" });
            await page.GetByRole(AriaRole.List, new() { Name = "Responsáveis encontrados para o processo" })
                .GetByRole(AriaRole.Button, new() { Name = responsible.Name })
                .ClickAsync();
            await Expect(page.GetByRole(AriaRole.Status)
                    .Filter(new() { HasText = "Responsável selecionado:" }))
                .ToContainTextAsync(responsible.Name);
            await page.GetByRole(AriaRole.Button, new() { Name = "Cadastrar", Exact = true }).ClickAsync();
            await Expect(page.GetByRole(AriaRole.Status)
                    .Filter(new() { HasText = "Processo cadastrado com sucesso." }))
                .ToBeVisibleAsync();
            await Expect(page.GetByRole(AriaRole.Link, new() { Name = processTitle }))
                .ToContainTextAsync(responsible.Name);

            await page.GotoAsync($"{organizationPath}/deadlines");
            await page.GetByRole(AriaRole.Button, new() { Name = "Cadastrar prazo" }).ClickAsync();
            ILocator processSearch = page.GetByLabel("Buscar processo", new() { Exact = true });
            await processSearch.FillAsync(processTitle);
            await processSearch.PressAsync("Enter");
            await page.GetByRole(AriaRole.Button, new() { Name = processTitle }).ClickAsync();

            // The form suggests the process responsible without being touched.
            await Expect(CreateFormResponsible(page)).ToHaveValueAsync("other");
            await Expect(page.GetByRole(AriaRole.List, new() { Name = "Responsáveis encontrados para o prazo" })
                    .GetByRole(AriaRole.Button, new() { Name = responsible.Name }))
                .ToHaveAttributeAsync("aria-pressed", "true");

            await page.GetByLabel("Título", new() { Exact = true }).FillAsync(deadlineTitle);
            await page.GetByLabel("Data do prazo", new() { Exact = true }).FillAsync(dueDate);
            await page.GetByRole(AriaRole.Button, new() { Name = "Cadastrar", Exact = true }).ClickAsync();
            await Expect(page.GetByRole(AriaRole.Status)
                    .Filter(new() { HasText = "Prazo cadastrado com sucesso." }))
                .ToBeVisibleAsync();
            ILocator deadlineLink = page.GetByRole(AriaRole.Link, new() { Name = deadlineTitle });
            await Expect(deadlineLink).ToContainTextAsync(responsible.Name);

            await deadlineLink.ClickAsync();
            await Expect(page.GetByRole(AriaRole.Heading, new() { Name = deadlineTitle }))
                .ToBeVisibleAsync();
            await Expect(DefinitionWithText(page, "Pendente")).ToBeVisibleAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Concluir", Exact = true }).ClickAsync();
            await Expect(page.GetByRole(AriaRole.Status)
                    .Filter(new() { HasText = "Prazo concluído com sucesso." }))
                .ToBeVisibleAsync();

            // The completed state comes back from the server after a reload.
            await page.ReloadAsync();
            await Expect(page.GetByRole(AriaRole.Heading, new() { Name = deadlineTitle }))
                .ToBeVisibleAsync();
            await Expect(DefinitionWithText(page, "Concluído")).ToBeVisibleAsync();
            await Expect(page.GetByRole(AriaRole.Term).Filter(new() { HasTextString = "Concluído em" }))
                .ToBeVisibleAsync();
            await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Reabrir", Exact = true }))
                .ToBeVisibleAsync();
            await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Concluir", Exact = true }))
                .ToHaveCountAsync(0);
        });
    }

    private static async Task CreateClientAsync(
        IPage page,
        string personType,
        string nameLabel,
        string name,
        string documentLabel,
        string document)
    {
        await page.GetByRole(AriaRole.Button, new() { Name = "Cadastrar cliente", Exact = true })
            .ClickAsync();
        ILocator personTypeButton = page.GetByRole(
            AriaRole.Button,
            new() { Name = personType, Exact = true });
        await personTypeButton.ClickAsync();
        await Expect(personTypeButton).ToHaveAttributeAsync("aria-pressed", "true");
        await page.GetByLabel(nameLabel, new() { Exact = true }).FillAsync(name);
        await page.GetByLabel(documentLabel, new() { Exact = true }).FillAsync(document);
        await page.GetByRole(AriaRole.Button, new() { Name = "Cadastrar", Exact = true }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Link, new() { Name = name })).ToBeVisibleAsync();
        await Expect(page.GetByRole(AriaRole.Status)
                .Filter(new() { HasText = "Cliente cadastrado com sucesso." }))
            .ToBeVisibleAsync();
    }

    // The list filters also have a "Responsável" select; only the create
    // form's one offers "Outra pessoa".
    private static ILocator CreateFormResponsible(IPage page)
    {
        return page.GetByRole(AriaRole.Combobox, new() { Name = "Responsável", Exact = true })
            .Filter(new()
            {
                Has = page.GetByRole(AriaRole.Option, new() { Name = "Outra pessoa" })
            });
    }

    private static ILocator DefinitionWithText(IPage page, string text)
    {
        return page.GetByRole(AriaRole.Definition)
            .Filter(new() { HasTextRegex = new Regex($"^\\s*{Regex.Escape(text)}\\s*$") });
    }
}
