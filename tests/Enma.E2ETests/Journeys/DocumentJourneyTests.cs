using System.Net;
using System.Security.Cryptography;
using Enma.E2ETests.Infrastructure;
using Microsoft.Playwright;
using Xunit.Abstractions;
using static Microsoft.Playwright.Assertions;

namespace Enma.E2ETests.Journeys;

// J5: the Owner uploads a PNG and a PDF, both fictitious and built in memory,
// previews each, downloads each byte for byte, has a malformed PDF and an
// unsupported file refused without anything new in the list, and deletes a
// document. From another organization, a document of this one must be
// indistinguishable from one that does not exist (as J9 does for clients).
[Collection(E2ECollection.Name)]
public sealed class DocumentJourneyTests : IClassFixture<E2EHost>
{
    private readonly E2EHost host;
    private readonly ITestOutputHelper output;

    public DocumentJourneyTests(E2EHost host, ITestOutputHelper output)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(output);
        this.host = host;
        this.output = output;
    }

    [Fact]
    public Task Owner_UploadsPreviewsDownloadsAndDeletesDocuments()
    {
        return BrowserJourney.RunAsync(host, async page =>
        {
            using SeededOwner owner = await host.Seeder.CreateVerifiedOwnerAsync("Teta");
            string suffix = Guid.NewGuid().ToString("N")[..8];
            var png = new SyntheticDocument($"imagem-e2e-{suffix}.png", "image/png", "Imagem PNG", SyntheticFiles.CreatePng());
            var pdf = new SyntheticDocument($"peticao-e2e-{suffix}.pdf", "application/pdf", "PDF", SyntheticFiles.CreatePdf());
            string malformedName = $"malformado-e2e-{suffix}.pdf";
            string unsupportedName = $"anotacoes-e2e-{suffix}.txt";
            string documentsPath = $"/organizations/{owner.OrganizationId:D}/documents";

            await owner.AuthenticateAsync(page.Context);
            await page.GotoAsync(documentsPath);
            await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Nenhum documento disponível" }))
                .ToBeVisibleAsync();

            foreach (SyntheticDocument document in new[] { png, pdf })
            {
                await OpenUploadAsync(page);
                await page.GetByLabel("Arquivo", new() { Exact = true })
                    .SetInputFilesAsync(document.Payload);
                await page.GetByRole(AriaRole.Button, new() { Name = "Enviar documento" }).ClickAsync();
                await Expect(StatusWithText(page, "Documento enviado com sucesso.")).ToBeVisibleAsync();

                ILocator row = DocumentRow(page, document.Name);
                await Expect(row.GetByRole(AriaRole.Cell, new() { Name = document.TypeLabel, Exact = true }))
                    .ToBeVisibleAsync();
                await Expect(row.GetByRole(AriaRole.Cell, new() { Name = document.SizeLabel, Exact = true }))
                    .ToBeVisibleAsync();
            }

            // The PNG preview decodes into an image; the PDF preview loads
            // the fetched bytes into its frame.
            await DocumentRow(page, png.Name).GetByRole(AriaRole.Button, new() { Name = "Visualizar" }).ClickAsync();
            ILocator pngPreview = page.GetByRole(AriaRole.Dialog, new() { Name = png.Name });
            ILocator image = pngPreview.GetByRole(AriaRole.Img, new() { Name = $"Pré-visualização de {png.Name}" });
            await Expect(image).ToBeVisibleAsync();
            await Expect(image).ToHaveJSPropertyAsync("naturalWidth", 2);
            await pngPreview.GetByRole(AriaRole.Button, new() { Name = "Fechar" }).ClickAsync();
            await Expect(pngPreview).ToHaveCountAsync(0);

            await DocumentRow(page, pdf.Name).GetByRole(AriaRole.Button, new() { Name = "Visualizar" }).ClickAsync();
            ILocator pdfPreview = page.GetByRole(AriaRole.Dialog, new() { Name = pdf.Name });
            ILocator frame = pdfPreview.GetByTitle($"Conteúdo de {pdf.Name}", new() { Exact = true });
            await Expect(frame).ToBeVisibleAsync();
            await Expect(frame).ToHaveAttributeAsync("src", new System.Text.RegularExpressions.Regex("^blob:"));
            await pdfPreview.GetByRole(AriaRole.Button, new() { Name = "Fechar" }).ClickAsync();
            await Expect(pdfPreview).ToHaveCountAsync(0);

            foreach (SyntheticDocument document in new[] { png, pdf })
            {
                IDownload download = await page.RunAndWaitForDownloadAsync(() =>
                    DocumentRow(page, document.Name)
                        .GetByRole(AriaRole.Link, new() { Name = "Baixar" })
                        .ClickAsync());
                Assert.Equal(document.Name, download.SuggestedFilename);
                await using Stream content = await download.CreateReadStreamAsync();
                Assert.Equal(
                    Convert.ToHexString(SHA256.HashData(document.Content)),
                    Convert.ToHexString(await SHA256.HashDataAsync(content)));
            }

            // A malformed PDF is refused by the server; an unsupported type is
            // refused before anything is sent.
            await OpenUploadAsync(page);
            ILocator fileInput = page.GetByLabel("Arquivo", new() { Exact = true });
            await fileInput.SetInputFilesAsync(new FilePayload
            {
                Name = malformedName,
                MimeType = "application/pdf",
                Buffer = SyntheticFiles.CreateMalformedPdf()
            });
            await page.GetByRole(AriaRole.Button, new() { Name = "Enviar documento" }).ClickAsync();
            await Expect(AlertWithText(page, "O arquivo ou a classificação não pôde ser aceito."))
                .ToBeVisibleAsync();

            await fileInput.SetInputFilesAsync(new FilePayload
            {
                Name = unsupportedName,
                MimeType = "text/plain",
                Buffer = "anotações fictícias"u8.ToArray()
            });
            await page.GetByRole(AriaRole.Button, new() { Name = "Enviar documento" }).ClickAsync();
            await Expect(AlertWithText(page, "Selecione um arquivo PDF, DOCX, XLSX, PNG, JPG ou JPEG."))
                .ToBeVisibleAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Cancelar" }).ClickAsync();

            await page.ReloadAsync();
            await Expect(DocumentRow(page, pdf.Name)).ToBeVisibleAsync();
            await Expect(page.GetByRole(AriaRole.Link, new() { Name = "Ver detalhes" })).ToHaveCountAsync(2);
            await Expect(page.GetByRole(AriaRole.Link, new() { Name = malformedName })).ToHaveCountAsync(0);
            await Expect(page.GetByRole(AriaRole.Link, new() { Name = unsupportedName })).ToHaveCountAsync(0);

            string pngPath = await page.GetByRole(AriaRole.Link, new() { Name = png.Name, Exact = true })
                .GetAttributeAsync("href")
                ?? throw new InvalidOperationException("The document link has no address.");
            Guid pngId = Guid.Parse(pngPath[(pngPath.LastIndexOf('/') + 1)..]);

            await DocumentRow(page, png.Name).GetByRole(AriaRole.Button, new() { Name = "Excluir" }).ClickAsync();
            ILocator confirmation = page.GetByRole(AriaRole.Alertdialog, new() { Name = "Excluir documento?" });
            await Expect(confirmation).ToContainTextAsync(png.Name);
            await confirmation.GetByRole(AriaRole.Button, new() { Name = "Excluir documento" }).ClickAsync();
            await Expect(StatusWithText(page, $"Documento “{png.Name}” excluído com sucesso."))
                .ToBeVisibleAsync();
            await Expect(page.GetByRole(AriaRole.Link, new() { Name = png.Name, Exact = true }))
                .ToHaveCountAsync(0);

            await page.ReloadAsync();
            await Expect(DocumentRow(page, pdf.Name)).ToBeVisibleAsync();
            await Expect(page.GetByRole(AriaRole.Link, new() { Name = png.Name, Exact = true }))
                .ToHaveCountAsync(0);
            Assert.Equal(
                HttpStatusCode.NotFound,
                (await owner.ObserveAsync(
                    $"api/organizations/{owner.OrganizationId:D}/documents/{pngId:D}/content")).Status);
        });
    }

    [Fact]
    public Task ForeignTenantDocument_IsIndistinguishableFromMissingOne()
    {
        return BrowserJourney.RunAsync(host, async page =>
        {
            using SeededOwner ownerA = await host.Seeder.CreateVerifiedOwnerAsync("Iota");
            using SeededOwner ownerB = await host.Seeder.CreateVerifiedOwnerAsync("Kappa");
            string documentName = $"contrato-iota-e2e-{Guid.NewGuid().ToString("N")[..8]}.pdf";
            Guid documentA = await ownerA.UploadDocumentAsync(
                documentName,
                "application/pdf",
                SyntheticFiles.CreatePdf());

            foreach (string resource in new[] { string.Empty, "/content", "/preview" })
            {
                ObservedResponse foreignOrganization = await ownerB.ObserveAsync(
                    $"api/organizations/{ownerA.OrganizationId:D}/documents/{documentA:D}{resource}");
                ObservedResponse missingOrganization = await ownerB.ObserveAsync(
                    $"api/organizations/{Guid.NewGuid():D}/documents/{documentA:D}{resource}");
                ObservedResponse foreignDocument = await ownerB.ObserveAsync(
                    $"api/organizations/{ownerB.OrganizationId:D}/documents/{documentA:D}{resource}");
                ObservedResponse missingDocument = await ownerB.ObserveAsync(
                    $"api/organizations/{ownerB.OrganizationId:D}/documents/{Guid.NewGuid():D}{resource}");

                string label = resource.Length == 0 ? "metadata" : resource.TrimStart('/');
                output.WriteLine($"{label}: foreign organization {foreignOrganization}");
                output.WriteLine($"{label}: missing organization {missingOrganization}");
                output.WriteLine($"{label}: foreign document     {foreignDocument}");
                output.WriteLine($"{label}: missing document     {missingDocument}");

                Assert.Equal(missingOrganization, foreignOrganization);
                Assert.Equal(HttpStatusCode.NotFound, foreignDocument.Status);
                Assert.Equal(missingDocument, foreignDocument);
            }

            await ownerB.AuthenticateAsync(page.Context);
            ILocator body = page.Locator("body");

            await page.GotoAsync($"/organizations/{ownerA.OrganizationId:D}/documents/{documentA:D}");
            await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Organização indisponível" }))
                .ToBeVisibleAsync();
            await Expect(body).Not.ToContainTextAsync(ownerA.OrganizationName);
            await Expect(body).Not.ToContainTextAsync(documentName);

            await page.GotoAsync($"/organizations/{ownerB.OrganizationId:D}/documents/{documentA:D}");
            await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Documento não encontrado ou indisponível" }))
                .ToBeVisibleAsync();
            await Expect(body).Not.ToContainTextAsync(ownerA.OrganizationName);
            await Expect(body).Not.ToContainTextAsync(documentName);

            await page.GotoAsync($"/organizations/{ownerB.OrganizationId:D}/documents");
            await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Nenhum documento disponível" }))
                .ToBeVisibleAsync();
            await Expect(body).Not.ToContainTextAsync(documentName);
        });
    }

    private static async Task OpenUploadAsync(IPage page)
    {
        await page.GetByRole(AriaRole.Button, new() { Name = "Enviar documento" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Enviar documento" }))
            .ToBeVisibleAsync();
    }

    private static ILocator DocumentRow(IPage page, string fileName)
    {
        return page.GetByRole(AriaRole.Row).Filter(new()
        {
            Has = page.GetByRole(AriaRole.Link, new() { Name = fileName, Exact = true })
        });
    }

    private static ILocator StatusWithText(IPage page, string text)
    {
        return page.GetByRole(AriaRole.Status).Filter(new() { HasTextString = text });
    }

    private static ILocator AlertWithText(IPage page, string text)
    {
        return page.GetByRole(AriaRole.Alert).Filter(new() { HasTextString = text });
    }

    private sealed record SyntheticDocument(
        string Name,
        string ContentType,
        string TypeLabel,
        byte[] Content)
    {
        // Both files stay under 1 KiB, which the list shows in bytes.
        public string SizeLabel => Content.Length < 1024
            ? $"{Content.Length} B"
            : throw new InvalidOperationException("The synthetic file outgrew the byte label.");

        public FilePayload Payload => new()
        {
            Name = Name,
            MimeType = ContentType,
            Buffer = Content
        };
    }
}
