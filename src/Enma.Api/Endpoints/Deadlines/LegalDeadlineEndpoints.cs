using System.Globalization;
using System.Security.Claims;
using Enma.Api.Authentication;
using Enma.Api.Authorization;
using Enma.Api.Contracts.Deadlines;
using Enma.Api.Endpoints;
using Enma.Application.Deadlines;
using Enma.Application.Deadlines.Complete;
using Enma.Application.Deadlines.Create;
using Enma.Application.Deadlines.GetById;
using Enma.Application.Deadlines.List;
using Enma.Application.Deadlines.Reopen;
using Enma.Application.Deadlines.Update;

namespace Enma.Api.Endpoints.Deadlines;

public static class LegalDeadlineEndpoints
{
    private const string RoutePrefix =
        "/api/organizations/{organizationId:guid}/deadlines";

    public static IEndpointRouteBuilder MapLegalDeadlineEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        RouteGroupBuilder group = endpoints
            .MapGroup(RoutePrefix)
            .WithTags("Deadlines")
            .RequireAuthorization(EnmaAuthorizationPolicies.OrganizationAccess)
            .RequireNoStoreResponses();

        group.MapPost(string.Empty, CreateAsync)
            .WithName("CreateLegalDeadline")
            .WithSummary("Creates a legal deadline in the contextual organization.")
            .Accepts<CreateLegalDeadlineRequest>("application/json")
            .Produces<CreateLegalDeadlineResponse>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .RequireEnmaAntiforgery();

        group.MapGet(string.Empty, ListAsync)
            .WithName("ListLegalDeadlines")
            .WithSummary("Lists legal deadlines in the contextual organization.")
            .Produces<ListLegalDeadlinesResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        group.MapGet("{deadlineId:guid}", GetAsync)
            .WithName("GetLegalDeadline")
            .WithSummary("Gets a legal deadline in the contextual organization.")
            .Produces<LegalDeadlineResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        group.MapPut("{deadlineId:guid}", UpdateAsync)
            .WithName("UpdateLegalDeadline")
            .WithSummary("Updates a pending legal deadline in the contextual organization.")
            .Accepts<UpdateLegalDeadlineRequest>("application/json")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .RequireEnmaAntiforgery();

        group.MapPost("{deadlineId:guid}/complete", CompleteAsync)
            .WithName("CompleteLegalDeadline")
            .WithSummary("Completes a legal deadline in the contextual organization.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .RequireEnmaAntiforgery();

        group.MapPost("{deadlineId:guid}/reopen", ReopenAsync)
            .WithName("ReopenLegalDeadline")
            .WithSummary("Reopens a legal deadline in the contextual organization.")
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
        CreateLegalDeadlineRequest request,
        ClaimsPrincipal principal,
        CreateLegalDeadlineUseCase useCase,
        CancellationToken cancellationToken)
    {
        if (!AuthenticatedUserId.TryGet(principal, out Guid userId))
        {
            return TypedResults.Unauthorized();
        }

        CreateLegalDeadlineResult result = await useCase.ExecuteAsync(
            userId,
            organizationId,
            request.ProcessId,
            request.Title,
            request.DueDate,
            cancellationToken);

        if (result.Status == CreateLegalDeadlineResultStatus.AccessDenied)
        {
            return TypedResults.Forbid();
        }

        if (result.Status ==
            CreateLegalDeadlineResultStatus.RelatedProcessUnavailable)
        {
            return TypedResults.NotFound();
        }

        Guid deadlineId = result.DeadlineId
            ?? throw new InvalidOperationException(
                "A successful legal deadline creation did not provide a deadline id.");
        string location = string.Create(
            CultureInfo.InvariantCulture,
            $"/api/organizations/{organizationId:D}/deadlines/{deadlineId:D}");

        return TypedResults.Created(
            location,
            new CreateLegalDeadlineResponse(deadlineId));
    }

    private static async Task<IResult> ListAsync(
        Guid organizationId,
        ClaimsPrincipal principal,
        ListLegalDeadlinesUseCase useCase,
        CancellationToken cancellationToken,
        int pageNumber = 1,
        int pageSize = ListLegalDeadlinesUseCase.DefaultPageSize)
    {
        if (!AuthenticatedUserId.TryGet(principal, out Guid userId))
        {
            return TypedResults.Unauthorized();
        }

        ListLegalDeadlinesResult result = await useCase.ExecuteAsync(
            userId,
            organizationId,
            pageNumber,
            pageSize,
            cancellationToken);

        if (result.Status == ListLegalDeadlinesResultStatus.AccessDenied)
        {
            return TypedResults.Forbid();
        }

        if (result.Status != ListLegalDeadlinesResultStatus.Succeeded)
        {
            throw new InvalidOperationException(
                "The legal deadline list returned an unknown status.");
        }

        LegalDeadlineListItemResponse[] items = result.Items
            .Select(MapListItem)
            .ToArray();

        return TypedResults.Ok(new ListLegalDeadlinesResponse(
            items,
            result.PageNumber,
            result.PageSize));
    }

    private static async Task<IResult> GetAsync(
        Guid organizationId,
        Guid deadlineId,
        ClaimsPrincipal principal,
        GetLegalDeadlineUseCase useCase,
        CancellationToken cancellationToken)
    {
        if (!AuthenticatedUserId.TryGet(principal, out Guid userId))
        {
            return TypedResults.Unauthorized();
        }

        GetLegalDeadlineResult result = await useCase.ExecuteAsync(
            userId,
            organizationId,
            deadlineId,
            cancellationToken);

        return result.Status switch
        {
            GetLegalDeadlineResultStatus.AccessDenied => TypedResults.Forbid(),
            GetLegalDeadlineResultStatus.NotFound => TypedResults.NotFound(),
            GetLegalDeadlineResultStatus.Succeeded => TypedResults.Ok(
                MapDetail(
                    result.LegalDeadline ?? throw new InvalidOperationException(
                        "A successful legal deadline query did not provide a deadline."))),
            _ => throw new InvalidOperationException(
                "The legal deadline query returned an unknown status.")
        };
    }

    private static async Task<IResult> UpdateAsync(
        Guid organizationId,
        Guid deadlineId,
        UpdateLegalDeadlineRequest request,
        ClaimsPrincipal principal,
        UpdateLegalDeadlineUseCase useCase,
        CancellationToken cancellationToken)
    {
        if (!AuthenticatedUserId.TryGet(principal, out Guid userId))
        {
            return TypedResults.Unauthorized();
        }

        UpdateLegalDeadlineResult result = await useCase.ExecuteAsync(
            userId,
            organizationId,
            deadlineId,
            request.Title,
            request.DueDate,
            cancellationToken);

        return result.Status switch
        {
            UpdateLegalDeadlineResultStatus.AccessDenied => TypedResults.Forbid(),
            UpdateLegalDeadlineResultStatus.NotFound => TypedResults.NotFound(),
            UpdateLegalDeadlineResultStatus.Conflict => TypedResults.Problem(
                title: "Resource conflict",
                detail: "The deadline cannot be edited in its current state.",
                statusCode: StatusCodes.Status409Conflict),
            UpdateLegalDeadlineResultStatus.Updated => TypedResults.NoContent(),
            _ => throw new InvalidOperationException(
                "The legal deadline update returned an unknown status.")
        };
    }

    private static async Task<IResult> CompleteAsync(
        Guid organizationId,
        Guid deadlineId,
        ClaimsPrincipal principal,
        CompleteLegalDeadlineUseCase useCase,
        CancellationToken cancellationToken)
    {
        if (!AuthenticatedUserId.TryGet(principal, out Guid userId))
        {
            return TypedResults.Unauthorized();
        }

        CompleteLegalDeadlineResult result = await useCase.ExecuteAsync(
            userId,
            organizationId,
            deadlineId,
            cancellationToken);

        return result.Status switch
        {
            CompleteLegalDeadlineResultStatus.AccessDenied => TypedResults.Forbid(),
            CompleteLegalDeadlineResultStatus.NotFound => TypedResults.NotFound(),
            CompleteLegalDeadlineResultStatus.Succeeded => TypedResults.NoContent(),
            _ => throw new InvalidOperationException(
                "The legal deadline completion returned an unknown status.")
        };
    }

    private static async Task<IResult> ReopenAsync(
        Guid organizationId,
        Guid deadlineId,
        ClaimsPrincipal principal,
        ReopenLegalDeadlineUseCase useCase,
        CancellationToken cancellationToken)
    {
        if (!AuthenticatedUserId.TryGet(principal, out Guid userId))
        {
            return TypedResults.Unauthorized();
        }

        ReopenLegalDeadlineResult result = await useCase.ExecuteAsync(
            userId,
            organizationId,
            deadlineId,
            cancellationToken);

        return result.Status switch
        {
            ReopenLegalDeadlineResultStatus.AccessDenied => TypedResults.Forbid(),
            ReopenLegalDeadlineResultStatus.NotFound => TypedResults.NotFound(),
            ReopenLegalDeadlineResultStatus.Succeeded => TypedResults.NoContent(),
            _ => throw new InvalidOperationException(
                "The legal deadline reopening returned an unknown status.")
        };
    }

    private static LegalDeadlineListItemResponse MapListItem(
        LegalDeadlineListItem legalDeadline)
    {
        return new LegalDeadlineListItemResponse(
            legalDeadline.Id,
            legalDeadline.Title,
            legalDeadline.DueDate,
            legalDeadline.ProcessId,
            legalDeadline.ProcessTitle,
            legalDeadline.ClientName,
            MapState(legalDeadline.State));
    }

    private static LegalDeadlineResponse MapDetail(
        LegalDeadlineDetailReadModel legalDeadline)
    {
        return new LegalDeadlineResponse(
            legalDeadline.Id,
            legalDeadline.Title,
            legalDeadline.DueDate,
            legalDeadline.ProcessId,
            legalDeadline.ProcessTitle,
            legalDeadline.ClientName,
            MapState(legalDeadline.State),
            legalDeadline.CreatedAt,
            legalDeadline.CompletedAt);
    }

    private static LegalDeadlineStateResponse MapState(
        LegalDeadlineReadState state)
    {
        return state switch
        {
            LegalDeadlineReadState.Pending => LegalDeadlineStateResponse.Pending,
            LegalDeadlineReadState.Completed => LegalDeadlineStateResponse.Completed,
            _ => throw new InvalidOperationException(
                "The legal deadline read model returned an unknown state.")
        };
    }
}
