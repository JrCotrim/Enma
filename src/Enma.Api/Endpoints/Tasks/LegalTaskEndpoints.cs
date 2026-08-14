using System.Globalization;
using System.Security.Claims;
using Enma.Api.Authentication;
using Enma.Api.Authorization;
using Enma.Api.Contracts.Tasks;
using Enma.Api.Endpoints;
using Enma.Application.Tasks;
using Enma.Application.Tasks.Assignment;
using Enma.Application.Tasks.Complete;
using Enma.Application.Tasks.Create;
using Enma.Application.Tasks.GetById;
using Enma.Application.Tasks.List;
using Enma.Application.Tasks.Reopen;
using Enma.Application.Tasks.Update;

namespace Enma.Api.Endpoints.Tasks;

public static class LegalTaskEndpoints
{
    private const string RoutePrefix =
        "/api/organizations/{organizationId:guid}/tasks";

    public static IEndpointRouteBuilder MapLegalTaskEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        RouteGroupBuilder group = endpoints
            .MapGroup(RoutePrefix)
            .WithTags("Tasks")
            .RequireAuthorization(EnmaAuthorizationPolicies.OrganizationAccess)
            .RequireNoStoreResponses();

        group.MapGet(string.Empty, ListAsync)
            .WithName("ListLegalTasks")
            .WithSummary("Lists legal tasks in the contextual organization.")
            .Produces<ListLegalTasksResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        group.MapGet("{taskId:guid}", GetAsync)
            .WithName("GetLegalTask")
            .WithSummary("Gets a legal task in the contextual organization.")
            .Produces<LegalTaskResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        group.MapPost(string.Empty, CreateAsync)
            .WithName("CreateLegalTask")
            .WithSummary("Creates a legal task in the contextual organization.")
            .Accepts<CreateLegalTaskRequest>("application/json")
            .Produces<CreateLegalTaskResponse>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .RequireEnmaAntiforgery();

        group.MapPut("{taskId:guid}", UpdateAsync)
            .WithName("UpdateLegalTask")
            .WithSummary("Updates a pending legal task in the contextual organization.")
            .Accepts<UpdateLegalTaskRequest>("application/json")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .RequireEnmaAntiforgery();

        group.MapPut("{taskId:guid}/assignee", ChangeAssigneeAsync)
            .WithName("ChangeLegalTaskAssignee")
            .WithSummary("Changes a pending legal task assignee in the contextual organization.")
            .Accepts<ChangeLegalTaskAssigneeRequest>("application/json")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .RequireEnmaAntiforgery();

        group.MapPost("{taskId:guid}/complete", CompleteAsync)
            .WithName("CompleteLegalTask")
            .WithSummary("Completes a legal task in the contextual organization.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .RequireEnmaAntiforgery();

        group.MapPost("{taskId:guid}/reopen", ReopenAsync)
            .WithName("ReopenLegalTask")
            .WithSummary("Reopens a legal task in the contextual organization.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .RequireEnmaAntiforgery();

        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        Guid organizationId,
        ClaimsPrincipal principal,
        ListLegalTasksUseCase useCase,
        CancellationToken cancellationToken,
        string? state = null,
        Guid? processId = null,
        string? assignee = null,
        int pageNumber = 1,
        int pageSize = ListLegalTasksUseCase.DefaultPageSize)
    {
        if (!AuthenticatedUserId.TryGet(principal, out Guid userId))
        {
            return TypedResults.Unauthorized();
        }

        if (!TryParseState(state, out LegalTaskState taskState) ||
            !TryParseAssignee(assignee, out LegalTaskAssigneeFilter assigneeFilter))
        {
            return TypedResults.BadRequest();
        }

        ListLegalTasksResult result = await useCase.ExecuteAsync(
            new ListLegalTasksQuery(
                userId,
                organizationId,
                taskState,
                processId,
                assigneeFilter,
                pageNumber,
                pageSize),
            cancellationToken);

        return result.Status switch
        {
            ListLegalTasksResultStatus.AccessDenied => TypedResults.Forbid(),
            ListLegalTasksResultStatus.InvalidInput => TypedResults.BadRequest(),
            ListLegalTasksResultStatus.Succeeded => TypedResults.Ok(
                new ListLegalTasksResponse(
                    result.Items.Select(MapListItem).ToArray(),
                    result.PageNumber,
                    result.PageSize,
                    result.HasNext)),
            _ => throw new InvalidOperationException(
                "The legal task list returned an unknown status.")
        };
    }

    private static async Task<IResult> GetAsync(
        Guid organizationId,
        Guid taskId,
        ClaimsPrincipal principal,
        GetLegalTaskUseCase useCase,
        CancellationToken cancellationToken)
    {
        if (!AuthenticatedUserId.TryGet(principal, out Guid userId))
        {
            return TypedResults.Unauthorized();
        }

        GetLegalTaskResult result = await useCase.ExecuteAsync(
            new GetLegalTaskQuery(userId, organizationId, taskId),
            cancellationToken);

        return result.Status switch
        {
            GetLegalTaskResultStatus.AccessDenied => TypedResults.Forbid(),
            GetLegalTaskResultStatus.NotFound => TypedResults.NotFound(),
            GetLegalTaskResultStatus.InvalidInput => TypedResults.BadRequest(),
            GetLegalTaskResultStatus.Succeeded => TypedResults.Ok(
                MapDetail(
                    result.LegalTask ?? throw new InvalidOperationException(
                        "A successful legal task query did not provide a task."))),
            _ => throw new InvalidOperationException(
                "The legal task query returned an unknown status.")
        };
    }

    private static async Task<IResult> CreateAsync(
        Guid organizationId,
        CreateLegalTaskRequest request,
        ClaimsPrincipal principal,
        CreateLegalTaskUseCase useCase,
        CancellationToken cancellationToken)
    {
        if (!AuthenticatedUserId.TryGet(principal, out Guid userId))
        {
            return TypedResults.Unauthorized();
        }

        CreateLegalTaskResult result = await useCase.ExecuteAsync(
            new CreateLegalTaskCommand(
                userId,
                organizationId,
                request.Title,
                request.Description,
                request.DueDate,
                request.ProcessId,
                request.AssigneeMembershipId),
            cancellationToken);

        if (result.Status == CreateLegalTaskResultStatus.Succeeded)
        {
            Guid taskId = result.LegalTaskId
                ?? throw new InvalidOperationException(
                    "A successful legal task creation did not provide a task id.");
            string location = string.Create(
                CultureInfo.InvariantCulture,
                $"/api/organizations/{organizationId:D}/tasks/{taskId:D}");

            return TypedResults.Created(
                location,
                new CreateLegalTaskResponse(taskId));
        }

        return result.Status switch
        {
            CreateLegalTaskResultStatus.AccessDenied => TypedResults.Forbid(),
            CreateLegalTaskResultStatus.InvalidInput => TypedResults.BadRequest(),
            CreateLegalTaskResultStatus.RelatedProcessUnavailable =>
                TypedResults.NotFound(),
            CreateLegalTaskResultStatus.RelatedAssigneeUnavailable =>
                CreateRelatedAssigneeUnavailableProblem(),
            _ => throw new InvalidOperationException(
                "The legal task creation returned an unknown status.")
        };
    }

    private static async Task<IResult> UpdateAsync(
        Guid organizationId,
        Guid taskId,
        UpdateLegalTaskRequest request,
        ClaimsPrincipal principal,
        UpdateLegalTaskUseCase useCase,
        CancellationToken cancellationToken)
    {
        if (!AuthenticatedUserId.TryGet(principal, out Guid userId))
        {
            return TypedResults.Unauthorized();
        }

        UpdateLegalTaskResult result = await useCase.ExecuteAsync(
            new UpdateLegalTaskCommand(
                userId,
                organizationId,
                taskId,
                request.Title,
                request.Description,
                request.DueDate,
                request.ProcessId),
            cancellationToken);

        return result switch
        {
            UpdateLegalTaskResult.AccessDenied => TypedResults.Forbid(),
            UpdateLegalTaskResult.NotFound => TypedResults.NotFound(),
            UpdateLegalTaskResult.RelatedProcessUnavailable =>
                TypedResults.NotFound(),
            UpdateLegalTaskResult.InvalidInput => TypedResults.BadRequest(),
            UpdateLegalTaskResult.Conflict => CreateTaskConflictProblem(),
            UpdateLegalTaskResult.Succeeded => TypedResults.NoContent(),
            _ => throw new InvalidOperationException(
                "The legal task update returned an unknown status.")
        };
    }

    private static async Task<IResult> ChangeAssigneeAsync(
        Guid organizationId,
        Guid taskId,
        ChangeLegalTaskAssigneeRequest request,
        ClaimsPrincipal principal,
        ChangeLegalTaskAssigneeUseCase useCase,
        CancellationToken cancellationToken)
    {
        if (!AuthenticatedUserId.TryGet(principal, out Guid userId))
        {
            return TypedResults.Unauthorized();
        }

        ChangeLegalTaskAssigneeResult result = await useCase.ExecuteAsync(
            new ChangeLegalTaskAssigneeCommand(
                userId,
                organizationId,
                taskId,
                request.AssigneeMembershipId),
            cancellationToken);

        return result switch
        {
            ChangeLegalTaskAssigneeResult.AccessDenied => TypedResults.Forbid(),
            ChangeLegalTaskAssigneeResult.NotFound => TypedResults.NotFound(),
            ChangeLegalTaskAssigneeResult.RelatedAssigneeUnavailable =>
                CreateRelatedAssigneeUnavailableProblem(),
            ChangeLegalTaskAssigneeResult.InvalidInput => TypedResults.BadRequest(),
            ChangeLegalTaskAssigneeResult.Conflict => CreateTaskConflictProblem(),
            ChangeLegalTaskAssigneeResult.Succeeded => TypedResults.NoContent(),
            _ => throw new InvalidOperationException(
                "The legal task assignment returned an unknown status.")
        };
    }

    private static async Task<IResult> CompleteAsync(
        Guid organizationId,
        Guid taskId,
        ClaimsPrincipal principal,
        CompleteLegalTaskUseCase useCase,
        CancellationToken cancellationToken)
    {
        if (!AuthenticatedUserId.TryGet(principal, out Guid userId))
        {
            return TypedResults.Unauthorized();
        }

        CompleteLegalTaskResult result = await useCase.ExecuteAsync(
            new CompleteLegalTaskCommand(userId, organizationId, taskId),
            cancellationToken);

        return result switch
        {
            CompleteLegalTaskResult.AccessDenied => TypedResults.Forbid(),
            CompleteLegalTaskResult.NotFound => TypedResults.NotFound(),
            CompleteLegalTaskResult.Succeeded => TypedResults.NoContent(),
            _ => throw new InvalidOperationException(
                "The legal task completion returned an unknown status.")
        };
    }

    private static async Task<IResult> ReopenAsync(
        Guid organizationId,
        Guid taskId,
        ClaimsPrincipal principal,
        ReopenLegalTaskUseCase useCase,
        CancellationToken cancellationToken)
    {
        if (!AuthenticatedUserId.TryGet(principal, out Guid userId))
        {
            return TypedResults.Unauthorized();
        }

        ReopenLegalTaskResult result = await useCase.ExecuteAsync(
            new ReopenLegalTaskCommand(userId, organizationId, taskId),
            cancellationToken);

        return result switch
        {
            ReopenLegalTaskResult.AccessDenied => TypedResults.Forbid(),
            ReopenLegalTaskResult.NotFound => TypedResults.NotFound(),
            ReopenLegalTaskResult.Succeeded => TypedResults.NoContent(),
            _ => throw new InvalidOperationException(
                "The legal task reopening returned an unknown status.")
        };
    }

    private static bool TryParseState(
        string? value,
        out LegalTaskState state)
    {
        if (string.IsNullOrEmpty(value) ||
            string.Equals(value, "pending", StringComparison.OrdinalIgnoreCase))
        {
            state = LegalTaskState.Pending;
            return true;
        }

        if (string.Equals(value, "completed", StringComparison.OrdinalIgnoreCase))
        {
            state = LegalTaskState.Completed;
            return true;
        }

        state = default;
        return false;
    }

    private static bool TryParseAssignee(
        string? value,
        out LegalTaskAssigneeFilter filter)
    {
        if (value is null ||
            string.Equals(value, "any", StringComparison.OrdinalIgnoreCase))
        {
            filter = LegalTaskAssigneeFilter.Any;
            return true;
        }

        if (string.Equals(value, "self", StringComparison.OrdinalIgnoreCase))
        {
            filter = LegalTaskAssigneeFilter.Self;
            return true;
        }

        if (string.Equals(value, "unassigned", StringComparison.OrdinalIgnoreCase))
        {
            filter = LegalTaskAssigneeFilter.Unassigned;
            return true;
        }

        if (Guid.TryParseExact(value, "D", out Guid membershipId) &&
            membershipId != Guid.Empty)
        {
            filter = LegalTaskAssigneeFilter.Membership(membershipId);
            return true;
        }

        filter = LegalTaskAssigneeFilter.Any;
        return false;
    }

    private static LegalTaskListItemResponse MapListItem(
        LegalTaskListItem legalTask)
    {
        return new LegalTaskListItemResponse(
            legalTask.Id,
            legalTask.Title,
            legalTask.DueDate,
            legalTask.ProcessId,
            legalTask.ProcessTitle,
            legalTask.ClientName,
            legalTask.AssigneeMembershipId,
            legalTask.AssigneeDisplayName,
            legalTask.CreatedByMembershipId,
            MapState(legalTask.State),
            legalTask.CreatedAt);
    }

    private static LegalTaskResponse MapDetail(
        LegalTaskDetailReadModel legalTask)
    {
        return new LegalTaskResponse(
            legalTask.Id,
            legalTask.Title,
            legalTask.Description,
            legalTask.DueDate,
            legalTask.ProcessId,
            legalTask.ProcessTitle,
            legalTask.ClientName,
            legalTask.AssigneeMembershipId,
            legalTask.AssigneeDisplayName,
            legalTask.CreatedByMembershipId,
            legalTask.CreatedByDisplayName,
            MapState(legalTask.State),
            legalTask.CreatedAt,
            legalTask.CompletedAt);
    }

    private static LegalTaskStateResponse MapState(LegalTaskState state)
    {
        return state switch
        {
            LegalTaskState.Pending => LegalTaskStateResponse.Pending,
            LegalTaskState.Completed => LegalTaskStateResponse.Completed,
            _ => throw new InvalidOperationException(
                "The legal task read model returned an unknown state.")
        };
    }

    private static IResult CreateRelatedAssigneeUnavailableProblem()
    {
        return TypedResults.Problem(
            title: "Related assignee unavailable",
            detail: "The requested assignee is unavailable.",
            statusCode: StatusCodes.Status400BadRequest);
    }

    private static IResult CreateTaskConflictProblem()
    {
        return TypedResults.Problem(
            title: "Resource conflict",
            detail: "The task cannot be changed in its current state.",
            statusCode: StatusCodes.Status409Conflict);
    }
}
