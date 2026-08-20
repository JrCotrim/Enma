using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Amazon.Runtime;
using Amazon.S3;
using Enma.Api.Contracts.Documents;
using Enma.Application.Authentication;
using Enma.Application.Documents.Storage;
using Enma.Domain.Authentication;
using Enma.Domain.Documents;
using Enma.Domain.Organizations;
using Enma.Domain.Users;
using Enma.Infrastructure.Documents.Storage;
using Enma.Infrastructure.Persistence;
using Enma.IntegrationTests.Infrastructure.Documents;
using Enma.IntegrationTests.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using HttpMediaTypeHeaderValue = System.Net.Http.Headers.MediaTypeHeaderValue;

namespace Enma.IntegrationTests.Api.Documents;

[Collection(PostgreSqlCollection.Name)]
public sealed class LegalDocumentUploadEndpointEndToEndTests : IAsyncLifetime
{
    private const string CsrfPath = "/api/auth/csrf";
    private const string SessionCookieName = "__Host-enma_session";
    private const string AntiforgeryCookieName = "__Host-enma_csrf";
    private const string CsrfHeaderName = "X-CSRF-TOKEN";
    private const string PasswordHash =
        "synthetic-document-upload-endpoint-password-hash";

    private static readonly DateTimeOffset Now = new(
        2026,
        8,
        20,
        21,
        0,
        0,
        TimeSpan.Zero);

    private readonly PostgreSqlFixture fixture;
    private readonly AmazonS3Client applicationClient;
    private readonly S3LegalDocumentStorage storage;
    private readonly EnmaApiFactory factory;
    private readonly HttpClient client;

    public LegalDocumentUploadEndpointEndToEndTests(PostgreSqlFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        this.fixture = fixture;

        DocumentStorageIntegrationEnvironment environment =
            DocumentStorageIntegrationEnvironment.Load();
        applicationClient = new AmazonS3Client(
            new BasicAWSCredentials(
                environment.AppAccessKey,
                environment.AppSecretKey),
            new AmazonS3Config
            {
                ServiceURL = environment.ServiceUrl,
                ForcePathStyle = true,
                AuthenticationRegion =
                    DocumentStorageIntegrationEnvironment.Region
            });
        storage = new S3LegalDocumentStorage(
            applicationClient,
            Options.Create(
                new DocumentStorageOptions
                {
                    ServiceUrl = environment.ServiceUrl,
                    BucketName =
                        DocumentStorageIntegrationEnvironment.BucketName,
                    Region =
                        DocumentStorageIntegrationEnvironment.Region,
                    ForcePathStyle = true,
                    AccessKey = environment.AppAccessKey,
                    SecretKey = environment.AppSecretKey,
                    RequireTls = false
                }));
        factory = new EnmaApiFactory(fixture, services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));
            services.RemoveAll<ILegalDocumentStorage>();
            services.AddSingleton<ILegalDocumentStorage>(storage);
        });
        client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false
        });
    }

    public Task InitializeAsync()
    {
        return fixture.ResetDatabaseAsync();
    }

    public async Task DisposeAsync()
    {
        client.Dispose();
        await factory.DisposeAsync();
        applicationClient.Dispose();
    }

    [Fact]
    public async Task UploadLegalDocument_ValidMultipart_PersistsPostgreSqlAndExactPrivateMinioObject()
    {
        var user = new User(
            "Document upload E2E user",
            $"document-upload-e2e-{Guid.NewGuid():N}@example.test",
            Now.AddHours(-2));
        user.VerifyEmail(Now.AddHours(-1));
        var organization = new Organization(
            "Document upload E2E",
            $"document-upload-e2e-{Guid.NewGuid():N}",
            Now.AddHours(-2));
        var membership = new OrganizationMembership(
            organization.Id,
            user.Id,
            OrganizationRole.Member,
            Now.AddHours(-1));
        string rawHandle = await SeedAuthenticatedUserAsync(
            user,
            organization,
            membership);
        CsrfPair csrf = await GetCsrfPairAsync(rawHandle);
        byte[] payload = CreateValidPdf();

        using HttpResponseMessage response = await SendUploadAsync(
            organization.Id,
            rawHandle,
            csrf,
            payload);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        UploadLegalDocumentResponse created = Assert.IsType<
            UploadLegalDocumentResponse>(
                await response.Content
                    .ReadFromJsonAsync<UploadLegalDocumentResponse>());

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        LegalDocument document = await dbContext.LegalDocuments
            .AsNoTracking()
            .SingleAsync(item => item.Id == created.Id);
        LegalDocumentStorageObjectKey objectKey =
            LegalDocumentStorageObjectKey.Parse(document.StoredObjectKey);

        try
        {
            Assert.Equal(organization.Id, document.OrganizationId);
            Assert.Equal(membership.Id, document.UploadedByMembershipId);
            Assert.Equal("evidence.pdf", document.OriginalFileName);
            Assert.Equal("application/pdf", document.ContentType);
            Assert.Equal(payload.LongLength, document.SizeBytes);
            Assert.Equal(
                SHA256.HashData(payload),
                document.ContentHashSha256.ToArray());
            Assert.Equal(Now, document.CreatedAt);

            await using ILegalDocumentStorageReadHandle handle =
                await storage.OpenReadAsync(objectKey);
            using var storedCopy = new MemoryStream();
            await handle.Content.CopyToAsync(storedCopy);
            Assert.Equal(payload, storedCopy.ToArray());

            string body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain(document.StoredObjectKey, body);
            Assert.DoesNotContain(
                "contentHashSha256",
                body,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                "bucket",
                body,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await storage.DeleteIfExistsAsync(objectKey);
        }
    }

    private async Task<string> SeedAuthenticatedUserAsync(
        User user,
        Organization organization,
        OrganizationMembership membership)
    {
        IAuthenticationSessionHandleService handleService = factory.Services
            .GetRequiredService<IAuthenticationSessionHandleService>();
        string rawHandle = handleService.GenerateHandle(out var secretHash);
        var credential = new UserCredential(
            user.Id,
            PasswordHash,
            Now.AddHours(-1));
        var session = new AuthenticationSession(
            user.Id,
            secretHash,
            credential.CredentialVersion,
            Now.AddMinutes(-30),
            Now.AddMinutes(10),
            Now.AddHours(2));

        await using EnmaDbContext dbContext = fixture.CreateDbContext();
        dbContext.Organizations.Add(organization);
        dbContext.Users.Add(user);
        dbContext.UserCredentials.Add(credential);
        dbContext.OrganizationMemberships.Add(membership);
        dbContext.AuthenticationSessions.Add(session);
        await dbContext.SaveChangesAsync();

        return rawHandle;
    }

    private async Task<CsrfPair> GetCsrfPairAsync(string rawHandle)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, CsrfPath);
        request.Headers.Add(
            HeaderNames.Cookie,
            $"{SessionCookieName}={rawHandle}");
        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        CsrfResponse result = Assert.IsType<CsrfResponse>(
            await response.Content.ReadFromJsonAsync<CsrfResponse>());
        SetCookieHeaderValue cookie = Assert.Single(
            ParseSetCookies(response),
            candidate => string.Equals(
                candidate.Name.ToString(),
                AntiforgeryCookieName,
                StringComparison.Ordinal));

        return new CsrfPair(result.RequestToken, cookie.Value.ToString());
    }

    private async Task<HttpResponseMessage> SendUploadAsync(
        Guid organizationId,
        string rawHandle,
        CsrfPair csrf,
        byte[] payload)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/organizations/{organizationId:D}/documents");
        request.Headers.Add(
            HeaderNames.Cookie,
            $"{SessionCookieName}={rawHandle}; {AntiforgeryCookieName}={csrf.CookieToken}");
        request.Headers.Add(CsrfHeaderName, csrf.RequestToken);

        using var form = new MultipartFormDataContent(
            $"enma-e2e-{Guid.NewGuid():N}");
        var fileContent = new ByteArrayContent(payload);
        fileContent.Headers.ContentType =
            HttpMediaTypeHeaderValue.Parse("application/pdf");
        form.Add(fileContent, "file", "evidence.pdf");
        request.Content = form;

        return await client.SendAsync(request);
    }

    private static IReadOnlyList<SetCookieHeaderValue> ParseSetCookies(
        HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues(
                HeaderNames.SetCookie,
                out IEnumerable<string>? values))
        {
            return [];
        }

        return SetCookieHeaderValue.ParseList(values.ToList()).ToArray();
    }

    private static byte[] CreateValidPdf()
    {
        return "%PDF-1.7\n1 0 obj\n<< /Type /Catalog >>\nendobj\nxref\n0 1\n0000000000 65535 f \ntrailer\n<< /Size 1 >>\nstartxref\n9\n%%EOF\n"u8.ToArray();
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return utcNow;
        }
    }

    private sealed record CsrfResponse(string RequestToken);

    private sealed record CsrfPair(string RequestToken, string CookieToken);
}
