using System.Globalization;
using System.Security.Claims;
using Enma.Api.Authentication;
using Enma.Api.Authorization;
using Enma.Api.Contracts.Documents;
using Enma.Application.Documents;
using Enma.Application.Documents.Download;
using Enma.Application.Documents.GetById;
using Enma.Application.Documents.Inspection;
using Enma.Application.Documents.List;
using Enma.Application.Documents.Upload;
using Microsoft.AspNetCore.Mvc;

namespace Enma.Api.Endpoints.Documents;

public static class LegalDocumentEndpoints
{
    private const string RoutePrefix =
        "/api/organizations/{organizationId:guid}/documents";

    private const long MultipartRequestOverheadAllowanceBytes = 64L * 1024L;

    private const long MaximumUploadRequestBodySizeBytes =
        LegalDocumentUploadPolicy.MaximumFileSizeBytes
        + MultipartRequestOverheadAllowanceBytes;

    public static IEndpointRouteBuilder MapLegalDocumentEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        RouteGroupBuilder group = endpoints
            .MapGroup(RoutePrefix)
            .WithTags("Documents")
            .RequireAuthorization(EnmaAuthorizationPolicies.OrganizationAccess)
            .RequireNoStoreResponses();

        group.MapPost(string.Empty, UploadAsync)
            .WithName("UploadLegalDocument")
            .WithSummary("Uploads one private legal document to the contextual organization.")
            .Accepts<UploadLegalDocumentRequest>("multipart/form-data")
            .Produces<UploadLegalDocumentResponse>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .WithMetadata(
                new RequestFormLimitsAttribute
                {
                    MemoryBufferThreshold = 64 * 1024,
                    MultipartBodyLengthLimit =
                        LegalDocumentUploadPolicy.MaximumFileSizeBytes
                })
            .WithMetadata(
                new RequestSizeLimitAttribute(
                    MaximumUploadRequestBodySizeBytes))
            .RequireEnmaAntiforgery();

        group.MapGet(string.Empty, ListAsync)
            .WithName("ListLegalDocuments")
            .WithSummary("Lists legal-document metadata in the contextual organization.")
            .Produces<ListLegalDocumentsResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        group.MapGet("{documentId:guid}", GetAsync)
            .WithName("GetLegalDocument")
            .WithSummary("Gets legal-document metadata in the contextual organization.")
            .Produces<LegalDocumentMetadataResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        group.MapGet("{documentId:guid}/content", DownloadAsync)
            .WithName("DownloadLegalDocument")
            .WithSummary("Downloads private legal-document content from the contextual organization.")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        return endpoints;
    }

    private static async Task<IResult> UploadAsync(
        Guid organizationId,
        [FromForm] UploadLegalDocumentRequest request,
        ClaimsPrincipal principal,
        UploadLegalDocumentUseCase useCase,
        HttpContext httpContext)
    {
        if (!AuthenticatedUserId.TryGet(principal, out Guid userId))
        {
            return TypedResults.Unauthorized();
        }

        IFormFile? file = request.File;

        if (file is null ||
            httpContext.Request.Form.Files.Count != 1 ||
            !string.Equals(file.Name, "file", StringComparison.OrdinalIgnoreCase) ||
            file.Length > LegalDocumentUploadPolicy.MaximumFileSizeBytes)
        {
            return TypedResults.BadRequest();
        }

        await using Stream content = file.OpenReadStream();

        UploadLegalDocumentResult result = await useCase.ExecuteAsync(
            new UploadLegalDocumentCommand(
                userId,
                organizationId,
                request.ClientId,
                request.ProcessId,
                file.FileName,
                file.ContentType,
                file.Length,
                content),
            httpContext.RequestAborted);

        if (result.Status == UploadLegalDocumentResultStatus.Succeeded)
        {
            Guid documentId = result.DocumentId
                ?? throw new InvalidOperationException(
                    "A successful legal-document upload did not provide a document id.");
            string location = string.Create(
                CultureInfo.InvariantCulture,
                $"/api/organizations/{organizationId:D}/documents/{documentId:D}");

            return TypedResults.Created(
                location,
                new UploadLegalDocumentResponse(documentId));
        }

        return result.Status switch
        {
            UploadLegalDocumentResultStatus.AccessDenied => TypedResults.Forbid(),
            UploadLegalDocumentResultStatus.InvalidInput => TypedResults.BadRequest(),
            UploadLegalDocumentResultStatus.Rejected => TypedResults.BadRequest(),
            UploadLegalDocumentResultStatus.RelatedClientUnavailable =>
                TypedResults.NotFound(),
            UploadLegalDocumentResultStatus.RelatedProcessUnavailable =>
                TypedResults.NotFound(),
            _ => throw new InvalidOperationException(
                "The legal-document upload returned an unknown status.")
        };
    }

    private static async Task<IResult> ListAsync(
        Guid organizationId,
        ClaimsPrincipal principal,
        ListLegalDocumentsUseCase useCase,
        CancellationToken cancellationToken,
        string? search = null,
        Guid? clientId = null,
        Guid? processId = null,
        int page = 1,
        int pageSize = ListLegalDocumentsUseCase.DefaultPageSize)
    {
        if (!AuthenticatedUserId.TryGet(principal, out Guid userId))
        {
            return TypedResults.Unauthorized();
        }

        ListLegalDocumentsResult result = await useCase.ExecuteAsync(
            new ListLegalDocumentsQuery(
                userId,
                organizationId,
                search,
                processId,
                clientId,
                page,
                pageSize),
            cancellationToken);

        return result.Status switch
        {
            ListLegalDocumentsResultStatus.AccessDenied => TypedResults.Forbid(),
            ListLegalDocumentsResultStatus.InvalidInput => TypedResults.BadRequest(),
            ListLegalDocumentsResultStatus.Succeeded => TypedResults.Ok(
                new ListLegalDocumentsResponse(
                    result.Items.Select(MapMetadata).ToArray(),
                    result.PageNumber,
                    result.PageSize,
                    result.HasNext)),
            _ => throw new InvalidOperationException(
                "The legal-document list returned an unknown status.")
        };
    }

    private static async Task<IResult> GetAsync(
        Guid organizationId,
        Guid documentId,
        ClaimsPrincipal principal,
        GetLegalDocumentUseCase useCase,
        CancellationToken cancellationToken)
    {
        if (!AuthenticatedUserId.TryGet(principal, out Guid userId))
        {
            return TypedResults.Unauthorized();
        }

        GetLegalDocumentResult result = await useCase.ExecuteAsync(
            new GetLegalDocumentQuery(userId, organizationId, documentId),
            cancellationToken);

        return result.Status switch
        {
            GetLegalDocumentResultStatus.AccessDenied => TypedResults.Forbid(),
            GetLegalDocumentResultStatus.NotFound => TypedResults.NotFound(),
            GetLegalDocumentResultStatus.InvalidInput => TypedResults.BadRequest(),
            GetLegalDocumentResultStatus.Succeeded => TypedResults.Ok(
                MapMetadata(
                    result.Document ?? throw new InvalidOperationException(
                        "A successful legal-document query did not provide a document."))),
            _ => throw new InvalidOperationException(
                "The legal-document query returned an unknown status.")
        };
    }

    private static async Task<IResult> DownloadAsync(
        Guid organizationId,
        Guid documentId,
        ClaimsPrincipal principal,
        DownloadLegalDocumentUseCase useCase,
        CancellationToken cancellationToken)
    {
        if (!AuthenticatedUserId.TryGet(principal, out Guid userId))
        {
            return TypedResults.Unauthorized();
        }

        DownloadLegalDocumentResult result = await useCase.ExecuteAsync(
            new DownloadLegalDocumentQuery(userId, organizationId, documentId),
            cancellationToken);

        return result.Status switch
        {
            DownloadLegalDocumentResultStatus.AccessDenied => TypedResults.Forbid(),
            DownloadLegalDocumentResultStatus.NotFound => TypedResults.NotFound(),
            DownloadLegalDocumentResultStatus.InvalidInput => TypedResults.BadRequest(),
            DownloadLegalDocumentResultStatus.ContentUnavailable =>
                CreateContentUnavailableProblem(),
            DownloadLegalDocumentResultStatus.Succeeded =>
                new LegalDocumentDownloadHttpResult(
                    result.Download ?? throw new InvalidOperationException(
                        "A successful legal-document download did not provide content.")),
            _ => throw new InvalidOperationException(
                "The legal-document download returned an unknown status.")
        };
    }

    private static LegalDocumentMetadataResponse MapMetadata(
        LegalDocumentMetadataReadModel document)
    {
        return new LegalDocumentMetadataResponse(
            document.Id,
            document.ClientId,
            document.ProcessId,
            document.OriginalFileName,
            document.ContentType,
            document.SizeBytes,
            document.CreatedAt);
    }

    private static IResult CreateContentUnavailableProblem()
    {
        return TypedResults.Problem(
            title: "Document content unavailable",
            detail: "The document content is temporarily unavailable.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    private sealed class LegalDocumentDownloadHttpResult(
        LegalDocumentDownload download) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);

            try
            {
                httpContext.Response.ContentLength = download.SizeBytes;
                IResult streamResult = Results.Stream(
                    responseStream => download.Content.CopyToAsync(
                        responseStream,
                        httpContext.RequestAborted),
                    download.ContentType,
                    download.OriginalFileName);

                await streamResult.ExecuteAsync(httpContext);
            }
            finally
            {
                await download.DisposeAsync();
            }
        }
    }
}
