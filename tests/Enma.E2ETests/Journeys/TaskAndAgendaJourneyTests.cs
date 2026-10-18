using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Enma.E2ETests.Infrastructure;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Enma.E2ETests.Journeys;

// J4: the Owner creates a task for another member, completes and reopens it;
// the Owner creates a calendar event, finds it on the right day of the month
// view, edits and deletes it. A third, plain Member sees both but has no
// control to change them, and the API refuses the change behind the hidden
// control. Browser contexts run in America/Sao_Paulo (BrowserJourney), which
// is what the Agenda shows times in.
[Collection(E2ECollection.Name)]
public sealed class TaskAndAgendaJourneyTests : IClassFixture<E2EHost>
{
    private static readonly CultureInfo BrazilianPortuguese = CultureInfo.GetCultureInfo("pt-BR");

    private static readonly TimeZoneInfo BrowserTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");

    private readonly E2EHost host;

    public TaskAndAgendaJourneyTests(E2EHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        this.host = host;
    }

    [Fact]
    public Task Owner_CreatesCompletesAndReopensTask_MemberCannotChangeIt()
    {
        return BrowserJourney.RunAsync(host, async page =>
        {
            using SeededOwner owner = await host.Seeder.CreateVerifiedOwnerAsync("Zeta");
            using SeededMember responsible = await host.Seeder.AddActiveMemberAsync(
                owner,
                "Zeta Responsavel",
                "Member");
            using SeededMember observer = await host.Seeder.AddActiveMemberAsync(
                owner,
                "Zeta Observador",
                "Member");
            string taskTitle = $"Tarefa Zeta E2E {Guid.NewGuid().ToString("N")[..8]}";
            DateOnly dueDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(7);
            string organizationPath = $"/organizations/{owner.OrganizationId:D}";

            await owner.AuthenticateAsync(page.Context);

            await page.GotoAsync($"{organizationPath}/tasks");
            await page.GetByRole(AriaRole.Button, new() { Name = "Nova tarefa" }).ClickAsync();
            await page.GetByLabel("Título", new() { Exact = true }).FillAsync(taskTitle);
            await page.GetByLabel("Prazo", new() { Exact = true })
                .FillAsync(dueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            await page.GetByRole(AriaRole.Combobox, new() { Name = "Responsável da tarefa" })
                .SelectOptionAsync(new SelectOptionValue { Label = "Outra pessoa" });
            ILocator memberSearch = page.GetByLabel("Buscar responsável para tarefa", new() { Exact = true });
            await memberSearch.FillAsync(responsible.Name);
            await memberSearch.PressAsync("Enter");
            await page.GetByRole(AriaRole.List, new() { Name = "Responsáveis encontrados para a tarefa" })
                .GetByRole(AriaRole.Button, new() { Name = responsible.Name })
                .ClickAsync();
            await Expect(StatusWithText(page, "Responsável selecionado:"))
                .ToContainTextAsync(responsible.Name);
            await page.GetByRole(AriaRole.Button, new() { Name = "Cadastrar tarefa" }).ClickAsync();
            await Expect(StatusWithText(page, "Tarefa cadastrada com sucesso.")).ToBeVisibleAsync();

            ILocator taskLink = page.GetByRole(AriaRole.Link, new() { Name = taskTitle });
            await Expect(taskLink).ToContainTextAsync(responsible.Name);
            await Expect(taskLink).ToContainTextAsync(
                dueDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture));
            await Expect(taskLink).ToContainTextAsync("Pendente");
            string taskPath = await taskLink.GetAttributeAsync("href")
                ?? throw new InvalidOperationException("The task link has no address.");
            Guid taskId = Guid.Parse(taskPath[(taskPath.LastIndexOf('/') + 1)..]);

            await taskLink.ClickAsync();
            await Expect(page.GetByRole(AriaRole.Heading, new() { Name = taskTitle }))
                .ToBeVisibleAsync();
            await Expect(DefinitionWithText(page, "Pendente")).ToBeVisibleAsync();
            await Expect(DefinitionWithText(page, responsible.Name)).ToBeVisibleAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Concluir tarefa" }).ClickAsync();
            await Expect(StatusWithText(page, "Tarefa concluída com sucesso.")).ToBeVisibleAsync();
            await Expect(DefinitionWithText(page, "Concluída")).ToBeVisibleAsync();

            // The completed task moves to the "Concluídas" list on the server.
            await page.GotoAsync($"{organizationPath}/tasks?state=completed");
            await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Concluídas" }))
                .ToHaveAttributeAsync("aria-pressed", "true");
            await Expect(taskLink).ToContainTextAsync("Concluída");

            await taskLink.ClickAsync();
            await Expect(page.GetByRole(AriaRole.Term).Filter(new() { HasTextString = "Concluída em" }))
                .ToBeVisibleAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Reabrir tarefa" }).ClickAsync();
            await Expect(StatusWithText(page, "Tarefa reaberta com sucesso.")).ToBeVisibleAsync();
            await page.ReloadAsync();
            await Expect(page.GetByRole(AriaRole.Heading, new() { Name = taskTitle }))
                .ToBeVisibleAsync();
            await Expect(DefinitionWithText(page, "Pendente")).ToBeVisibleAsync();
            await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Concluir tarefa" }))
                .ToBeVisibleAsync();

            // A Member who is neither the responsible nor the creator sees the
            // task but gets no control to change it.
            await using IBrowserContext observerContext = await BrowserJourney.NewContextAsync(host);
            await observer.AuthenticateAsync(observerContext);
            IPage observerPage = await observerContext.NewPageAsync();
            await observerPage.GotoAsync($"{organizationPath}/tasks");
            ILocator observerTaskLink = observerPage.GetByRole(AriaRole.Link, new() { Name = taskTitle });
            await Expect(observerTaskLink).ToContainTextAsync(responsible.Name);
            await observerTaskLink.ClickAsync();
            await Expect(observerPage.GetByRole(AriaRole.Heading, new() { Name = taskTitle }))
                .ToBeVisibleAsync();
            await Expect(DefinitionWithText(observerPage, responsible.Name)).ToBeVisibleAsync();
            foreach (string control in new[]
                     {
                         "Editar tarefa", "Concluir tarefa", "Reabrir tarefa",
                         "Alterar responsável", "Assumir tarefa", "Liberar tarefa"
                     })
            {
                await Expect(observerPage.GetByRole(AriaRole.Button, new() { Name = control }))
                    .ToHaveCountAsync(0);
            }

            await observer.SendAsync(
                HttpMethod.Post,
                $"api/organizations/{owner.OrganizationId:D}/tasks/{taskId:D}/complete",
                null,
                HttpStatusCode.Forbidden);
            await observerPage.ReloadAsync();
            await Expect(observerPage.GetByRole(AriaRole.Heading, new() { Name = taskTitle }))
                .ToBeVisibleAsync();
            await Expect(DefinitionWithText(observerPage, "Pendente")).ToBeVisibleAsync();
        });
    }

    [Fact]
    public Task Owner_CreatesEditsAndDeletesEventOnBrowserDay_MemberCannotChangeIt()
    {
        return BrowserJourney.RunAsync(host, async page =>
        {
            using SeededOwner owner = await host.Seeder.CreateVerifiedOwnerAsync("Eta");
            using SeededMember observer = await host.Seeder.AddActiveMemberAsync(
                owner,
                "Eta Observador",
                "Member");
            string suffix = Guid.NewGuid().ToString("N")[..8];
            string eventTitle = $"Audiência Eta E2E {suffix}";
            string editedTitle = $"Audiência remarcada Eta E2E {suffix}";
            string organizationPath = $"/organizations/{owner.OrganizationId:D}";

            // 22:00 in the browser zone is already the next day in UTC: the
            // event must still land on the browser's day.
            DateTime browserToday = TimeZoneInfo.ConvertTime(DateTime.UtcNow, BrowserTimeZone);
            DateOnly eventDay = new DateOnly(browserToday.Year, browserToday.Month, 1)
                .AddMonths(1)
                .AddDays(14);
            DateTimeOffset startsAt = AtBrowserTime(eventDay, 22, 0);
            DateTimeOffset editedStartsAt = AtBrowserTime(eventDay, 20, 30);
            Assert.NotEqual(eventDay, DateOnly.FromDateTime(startsAt.UtcDateTime));

            await owner.AuthenticateAsync(page.Context);

            await page.GotoAsync($"{organizationPath}/agenda");
            await page.GetByRole(AriaRole.Button, new() { Name = "Novo evento" }).ClickAsync();
            await page.GetByLabel("Título", new() { Exact = true }).FillAsync(eventTitle);
            await page.GetByLabel("Início", new() { Exact = true }).FillAsync(LocalInput(eventDay, 22, 0));
            await page.GetByLabel("Término", new() { Exact = true }).FillAsync(LocalInput(eventDay, 23, 0));
            await page.GetByLabel("Local", new() { Exact = true }).FillAsync("Sala 3 (fictícia)");
            await page.GetByRole(AriaRole.Button, new() { Name = "Criar evento" }).ClickAsync();
            await Expect(StatusWithText(page, "Evento criado com sucesso.")).ToBeVisibleAsync();

            await OpenMonthAsync(page, eventDay);
            await Expect(DayButton(page, eventDay, 1)).ToBeVisibleAsync();
            await Expect(DayButton(page, eventDay.AddDays(1), 0)).ToBeVisibleAsync();
            await DayButton(page, eventDay, 1).ClickAsync();
            ILocator eventButton = DayItems(page)
                .GetByRole(AriaRole.Button, new() { Name = $"Evento: {eventTitle}" });
            await Expect(eventButton).ToContainTextAsync("22:00");

            // The server stores the browser-local time as the right instant.
            JsonObject storedEvent = await FindAgendaEventAsync(owner, eventDay, eventTitle);
            Assert.Equal(
                startsAt,
                DateTimeOffset.Parse(
                    storedEvent["startsAt"]!.GetValue<string>(),
                    CultureInfo.InvariantCulture));
            Guid eventId = Guid.Parse(storedEvent["id"]!.GetValue<string>());

            await eventButton.ClickAsync();
            await Expect(page.GetByRole(AriaRole.Heading, new() { Name = eventTitle }))
                .ToBeVisibleAsync();
            await Expect(DefinitionAt(page, eventDay, "22:00")).ToBeVisibleAsync();
            await Expect(DefinitionWithText(page, "Sala 3 (fictícia)")).ToBeVisibleAsync();

            await page.GetByRole(AriaRole.Button, new() { Name = "Editar evento" }).ClickAsync();
            await page.GetByLabel("Título", new() { Exact = true }).FillAsync(editedTitle);
            await page.GetByLabel("Início", new() { Exact = true }).FillAsync(LocalInput(eventDay, 20, 30));
            await page.GetByLabel("Término", new() { Exact = true }).FillAsync(LocalInput(eventDay, 21, 30));
            await page.GetByRole(AriaRole.Button, new() { Name = "Salvar alterações" }).ClickAsync();
            // Shown both above the calendar and in the event panel.
            await Expect(StatusWithText(page, "Evento atualizado com sucesso.").First).ToBeVisibleAsync();
            await Expect(page.GetByRole(AriaRole.Heading, new() { Name = editedTitle }))
                .ToBeVisibleAsync();
            await Expect(DefinitionAt(page, eventDay, "20:30")).ToBeVisibleAsync();
            await Expect(DayItems(page).GetByRole(AriaRole.Button, new() { Name = $"Evento: {editedTitle}" }))
                .ToContainTextAsync("20:30");
            Assert.Equal(
                editedStartsAt,
                DateTimeOffset.Parse(
                    (await FindAgendaEventAsync(owner, eventDay, editedTitle))["startsAt"]!.GetValue<string>(),
                    CultureInfo.InvariantCulture));

            // A Member who did not create the event sees it but gets no
            // control to change it.
            await using (IBrowserContext observerContext = await BrowserJourney.NewContextAsync(host))
            {
                await observer.AuthenticateAsync(observerContext);
                IPage observerPage = await observerContext.NewPageAsync();
                await observerPage.GotoAsync($"{organizationPath}/agenda");
                await OpenMonthAsync(observerPage, eventDay);
                await DayButton(observerPage, eventDay, 1).ClickAsync();
                await DayItems(observerPage)
                    .GetByRole(AriaRole.Button, new() { Name = $"Evento: {editedTitle}" })
                    .ClickAsync();
                await Expect(observerPage.GetByRole(AriaRole.Heading, new() { Name = editedTitle }))
                    .ToBeVisibleAsync();
                await Expect(DefinitionWithText(observerPage, owner.OwnerName)).ToBeVisibleAsync();
                await Expect(DefinitionAt(observerPage, eventDay, "20:30")).ToBeVisibleAsync();
                foreach (string control in new[] { "Editar evento", "Alterar responsável", "Excluir evento" })
                {
                    await Expect(observerPage.GetByRole(AriaRole.Button, new() { Name = control }))
                        .ToHaveCountAsync(0);
                }

                await observer.SendAsync(
                    HttpMethod.Delete,
                    $"api/organizations/{owner.OrganizationId:D}/calendar-events/{eventId:D}",
                    null,
                    HttpStatusCode.Forbidden);
            }

            await page.GetByRole(AriaRole.Button, new() { Name = "Excluir evento" }).ClickAsync();
            await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Excluir este evento?" }))
                .ToBeVisibleAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Confirmar exclusão" }).ClickAsync();
            await Expect(StatusWithText(page, "Evento excluído com sucesso.")).ToBeVisibleAsync();
            await Expect(DayButton(page, eventDay, 0)).ToBeVisibleAsync();

            await page.ReloadAsync();
            await OpenMonthAsync(page, eventDay);
            await Expect(DayButton(page, eventDay, 0)).ToBeVisibleAsync();
            await Expect(page.GetByRole(AriaRole.Button, new() { Name = $"Evento: {editedTitle}" }))
                .ToHaveCountAsync(0);
        });
    }

    // The Agenda opens on the browser's current month; the event is always
    // in the next one. Waiting for the current month to settle first keeps
    // the page from aborting a request in flight.
    private static async Task OpenMonthAsync(IPage page, DateOnly day)
    {
        await Expect(page.GetByRole(AriaRole.Region, new() { Name = "Navegação mensal da agenda" }))
            .ToHaveAttributeAsync("aria-busy", "false");
        await page.GetByRole(AriaRole.Button, new() { Name = "Próximo mês" }).ClickAsync();
        string month = day.ToString("MMMM 'de' yyyy", BrazilianPortuguese);
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = char.ToUpperInvariant(month[0]) + month[1..], Exact = true }))
            .ToBeVisibleAsync();
    }

    // Day cells are named "domingo, 15 de novembro de 2026, 1 compromisso".
    private static ILocator DayButton(IPage page, DateOnly day, int count)
    {
        string date = day.ToString("dddd, d 'de' MMMM 'de' yyyy", BrazilianPortuguese);
        string items = count == 1 ? "1 compromisso" : $"{count} compromissos";
        return page.GetByRole(AriaRole.Button, new() { Name = $"{date}, {items}", Exact = true });
    }

    private static ILocator DayItems(IPage page)
    {
        return page.GetByRole(AriaRole.Region, new() { Name = "Compromissos da data selecionada" });
    }

    private static async Task<JsonObject> FindAgendaEventAsync(
        SeededOwner owner,
        DateOnly day,
        string title)
    {
        string from = Uri.EscapeDataString(AtBrowserTime(day, 0, 0).ToString("O", CultureInfo.InvariantCulture));
        string to = Uri.EscapeDataString(AtBrowserTime(day.AddDays(1), 0, 0).ToString("O", CultureInfo.InvariantCulture));
        ObservedResponse agenda = await owner.ObserveAsync(
            $"api/organizations/{owner.OrganizationId:D}/agenda?from={from}&to={to}");
        Assert.Equal(HttpStatusCode.OK, agenda.Status);

        return Assert.Single(
                JsonNode.Parse(agenda.ComparableBody)!["items"]!.AsArray(),
                item => item?["title"]?.GetValue<string>() == title)!
            .AsObject();
    }

    private static DateTimeOffset AtBrowserTime(DateOnly day, int hour, int minute)
    {
        var local = day.ToDateTime(new TimeOnly(hour, minute));
        return new DateTimeOffset(local, BrowserTimeZone.GetUtcOffset(local));
    }

    private static string LocalInput(DateOnly day, int hour, int minute)
    {
        return day.ToDateTime(new TimeOnly(hour, minute))
            .ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture);
    }

    private static string ShortDate(DateOnly day)
    {
        return day.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
    }

    private static ILocator StatusWithText(IPage page, string text)
    {
        return page.GetByRole(AriaRole.Status).Filter(new() { HasTextString = text });
    }

    // The event panel's "Início" (or "Término") value, e.g. "15/11/2026, 22:00".
    private static ILocator DefinitionAt(IPage page, DateOnly day, string time)
    {
        return page.GetByRole(AriaRole.Definition).Filter(new()
        {
            HasTextRegex = new Regex($"^\\s*{Regex.Escape(ShortDate(day))}\\D+{Regex.Escape(time)}\\s*$")
        });
    }

    private static ILocator DefinitionWithText(IPage page, string text)
    {
        return page.GetByRole(AriaRole.Definition)
            .Filter(new() { HasTextRegex = new Regex($"^\\s*{Regex.Escape(text)}\\s*$") });
    }
}
