using System.Globalization;
using System.Security.Claims;
using Enma.Api.Authentication;
using Enma.Api.Authorization;
using Enma.Api.Contracts.Processes;
using Enma.Api.Endpoints;
using Enma.Application.Processes;
using Enma.Application.Processes.Create;
using Enma.Application.Processes.GetById;
using Enma.Application.Processes.List;
using Enma.Application.Processes.Lookup;
using Enma.Application.Processes.Update;

namespace Enma.Api.Endpoints.Processes;

public static class LegalProcessEndpoints
{
    private const string RoutePrefix =
        "/api/organizations/{organizationId:guid}/processes";

    public static IEndpointRouteBuilder MapLegalProcessEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        RouteGroupBuilder group = endpoints
            .MapGroup(RoutePrefix)
            .WithTags("Processes")
            .RequireAuthorization(EnmaAuthorizationPolicies.OrganizationAccess)
            .RequireNoStoreResponses();

        group.MapPost(string.Empty, CreateAsync)
            .WithName("CreateLegalProcess")
            .WithSummary("Creates a legal process in the contextual organization.")
            .Accepts<CreateLegalProcessRequest>("application/json")
            .Produces<CreateLegalProcessResponse>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .RequireEnmaAntiforgery();

        group.MapGet(string.Empty, ListAsync)
            .WithName("ListLegalProcesses")
            .WithSummary("Lists legal processes in the contextual organization.")
            .Produces<ListLegalProcessesResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        group.MapGet("lookup", LookupAsync)
            .WithName("LookupLegalProcesses")
            .WithSummary("Finds legal processes in the contextual organization.")
            .Produces<LegalProcessLookupResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        group.MapGet("{processId:guid}", GetAsync)
            .WithName("GetLegalProcess")
            .WithSummary("Gets a legal process in the contextual organization.")
            .Produces<LegalProcessResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        group.MapPut("{processId:guid}", UpdateAsync)
            .WithName("UpdateLegalProcess")
            .WithSummary("Updates a legal process in the contextual organization.")
            .Accepts<UpdateLegalProcessRequest>("application/json")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .RequireEnmaAntiforgery();

        return endpoints;
    }

    private static async Task<IResult> CreateAsync(
        Guid organizationId,
        CreateLegalProcessRequest request,
        ClaimsPrincipal principal,
        CreateLegalProcessUseCase useCase,
        CancellationToken cancellationToken)
    {
        if (!AuthenticatedUserId.TryGet(principal, out Guid userId))
        {
            return TypedResults.Unauthorized();
        }

        CreateLegalProcessResult result = await useCase.ExecuteAsync(
            userId,
            organizationId,
            request.ClientId,
            request.Title,
            cancellationToken);

        if (result.Status == CreateLegalProcessResultStatus.AccessDenied)
        {
            return TypedResults.Forbid();
        }

        if (result.Status ==
            CreateLegalProcessResultStatus.RelatedClientUnavailable)
        {
            return TypedResults.NotFound();
        }

        Guid processId = result.ProcessId
            ?? throw new InvalidOperationException(
                "A successful legal process creation did not provide a process id.");
        string location = string.Create(
            CultureInfo.InvariantCulture,
            $"/api/organizations/{organizationId:D}/processes/{processId:D}");

        return TypedResults.Created(
            location,
            new CreateLegalProcessResponse(processId));
    }

    private static async Task<IResult> GetAsync(
        Guid organizationId,
        Guid processId,
        ClaimsPrincipal principal,
        GetLegalProcessUseCase useCase,
        CancellationToken cancellationToken)
    {
        if (!AuthenticatedUserId.TryGet(principal, out Guid userId))
        {
            return TypedResults.Unauthorized();
        }

        GetLegalProcessResult result = await useCase.ExecuteAsync(
            userId,
            organizationId,
            processId,
            cancellationToken);

        return result.Status switch
        {
            GetLegalProcessResultStatus.AccessDenied => TypedResults.Forbid(),
            GetLegalProcessResultStatus.NotFound => TypedResults.NotFound(),
            GetLegalProcessResultStatus.Succeeded => TypedResults.Ok(
                MapLegalProcess(
                    result.LegalProcess ?? throw new InvalidOperationException(
                        "A successful legal process query did not provide a process."))),
            _ => throw new InvalidOperationException(
                "The legal process query returned an unknown status.")
        };
    }

    private static async Task<IResult> ListAsync(
        Guid organizationId,
        ClaimsPrincipal principal,
        ListLegalProcessesUseCase useCase,
        CancellationToken cancellationToken,
        int pageNumber = 1,
        int pageSize = ListLegalProcessesUseCase.DefaultPageSize)
    {
        if (!AuthenticatedUserId.TryGet(principal, out Guid userId))
        {
            return TypedResults.Unauthorized();
        }

        ListLegalProcessesResult result = await useCase.ExecuteAsync(
            userId,
            organizationId,
            pageNumber,
            pageSize,
            cancellationToken);

        if (result.Status == ListLegalProcessesResultStatus.AccessDenied)
        {
            return TypedResults.Forbid();
        }

        LegalProcessResponse[] items = result.Items
            .Select(MapLegalProcess)
            .ToArray();

        return TypedResults.Ok(new ListLegalProcessesResponse(
            items,
            result.PageNumber,
            result.PageSize));
    }

    private static async Task<IResult> LookupAsync(
        Guid organizationId,
        ClaimsPrincipal principal,
        SearchLegalProcessesUseCase useCase,
        CancellationToken cancellationToken,
        string? search = null,
        int pageNumber = 1,
        int pageSize = SearchLegalProcessesUseCase.DefaultPageSize)
    {
        if (!AuthenticatedUserId.TryGet(principal, out Guid userId))
        {
            return TypedResults.Unauthorized();
        }

        SearchLegalProcessesResult result = await useCase.ExecuteAsync(
            userId,
            organizationId,
            search,
            pageNumber,
            pageSize,
            cancellationToken);

        if (result.Status == SearchLegalProcessesResultStatus.AccessDenied)
        {
            return TypedResults.Forbid();
        }

        LegalProcessLookupItemResponse[] items = result.Items
            .Select(legalProcess => new LegalProcessLookupItemResponse(
                legalProcess.Id,
                legalProcess.Title,
                legalProcess.ClientName))
            .ToArray();

        return TypedResults.Ok(new LegalProcessLookupResponse(
            items,
            result.PageNumber,
            result.PageSize,
            result.HasNext));
    }

    private static async Task<IResult> UpdateAsync(
        Guid organizationId,
        Guid processId,
        UpdateLegalProcessRequest request,
        ClaimsPrincipal principal,
        UpdateLegalProcessUseCase useCase,
        CancellationToken cancellationToken)
    {
        if (!AuthenticatedUserId.TryGet(principal, out Guid userId))
        {
            return TypedResults.Unauthorized();
        }

        UpdateLegalProcessResult result = await useCase.ExecuteAsync(
            userId,
            organizationId,
            processId,
            request.Title,
            cancellationToken);

        return result.Status switch
        {
            UpdateLegalProcessResultStatus.AccessDenied => TypedResults.Forbid(),
            UpdateLegalProcessResultStatus.NotFound => TypedResults.NotFound(),
            UpdateLegalProcessResultStatus.Updated => TypedResults.NoContent(),
            _ => throw new InvalidOperationException(
                "The legal process update returned an unknown status.")
        };
    }

    private static LegalProcessResponse MapLegalProcess(
        LegalProcessReadModel legalProcess)
    {
        return new LegalProcessResponse(
            legalProcess.Id,
            legalProcess.Title,
            legalProcess.ClientId,
            legalProcess.ClientName,
            legalProcess.CreatedAt);
    }
}
