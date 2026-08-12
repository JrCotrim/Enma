using System.Globalization;
using System.Security.Claims;
using Enma.Api.Authentication;
using Enma.Api.Authorization;
using Enma.Api.Contracts.Clients;
using Enma.Application.Clients;
using Enma.Application.Clients.Create;
using Enma.Application.Clients.Deactivate;
using Enma.Application.Clients.GetById;
using Enma.Application.Clients.List;
using Enma.Application.Clients.Reactivate;
using Enma.Application.Clients.Update;

namespace Enma.Api.Endpoints.Clients;

public static class ClientEndpoints
{
    private const string RoutePrefix =
        "/api/organizations/{organizationId:guid}/clients";

    public static IEndpointRouteBuilder MapClientEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        RouteGroupBuilder group = endpoints
            .MapGroup(RoutePrefix)
            .WithTags("Clients")
            .RequireAuthorization(EnmaAuthorizationPolicies.OrganizationAccess)
            .RequireClientNoStoreResponses();

        group.MapPost(string.Empty, CreateAsync)
            .WithName("CreateClient")
            .WithSummary("Creates a client in the contextual organization.")
            .Accepts<CreateClientRequest>("application/json")
            .Produces<CreateClientResponse>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .RequireEnmaAntiforgery();

        group.MapGet("{clientId:guid}", GetAsync)
            .WithName("GetClient")
            .WithSummary("Gets a client in the contextual organization.")
            .Produces<ClientResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        group.MapGet(string.Empty, ListAsync)
            .WithName("ListClients")
            .WithSummary("Lists clients in the contextual organization.")
            .Produces<ListClientsResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        group.MapPut("{clientId:guid}", UpdateAsync)
            .WithName("UpdateClient")
            .WithSummary("Updates a client in the contextual organization.")
            .Accepts<UpdateClientRequest>("application/json")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .RequireEnmaAntiforgery();

        group.MapPost("{clientId:guid}/deactivate", DeactivateAsync)
            .WithName("DeactivateClient")
            .WithSummary("Deactivates a client in the contextual organization.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .RequireEnmaAntiforgery();

        group.MapPost("{clientId:guid}/reactivate", ReactivateAsync)
            .WithName("ReactivateClient")
            .WithSummary("Reactivates a client in the contextual organization.")
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
        CreateClientRequest request,
        ClaimsPrincipal principal,
        CreateClientUseCase useCase,
        CancellationToken cancellationToken)
    {
        if (!AuthenticatedUserId.TryGet(principal, out Guid userId))
        {
            return TypedResults.Unauthorized();
        }

        CreateClientResult result = await useCase.ExecuteAsync(
            userId,
            organizationId,
            request.Name,
            cancellationToken);

        if (result.Status == CreateClientResultStatus.AccessDenied)
        {
            return TypedResults.Forbid();
        }

        Guid clientId = result.ClientId
            ?? throw new InvalidOperationException(
                "A successful client creation did not provide a client id.");
        string location = string.Create(
            CultureInfo.InvariantCulture,
            $"/api/organizations/{organizationId:D}/clients/{clientId:D}");

        return TypedResults.Created(
            location,
            new CreateClientResponse(clientId));
    }

    private static async Task<IResult> GetAsync(
        Guid organizationId,
        Guid clientId,
        ClaimsPrincipal principal,
        GetClientUseCase useCase,
        CancellationToken cancellationToken)
    {
        if (!AuthenticatedUserId.TryGet(principal, out Guid userId))
        {
            return TypedResults.Unauthorized();
        }

        GetClientResult result = await useCase.ExecuteAsync(
            userId,
            organizationId,
            clientId,
            cancellationToken);

        return result.Status switch
        {
            GetClientResultStatus.AccessDenied => TypedResults.Forbid(),
            GetClientResultStatus.NotFound => TypedResults.NotFound(),
            GetClientResultStatus.Succeeded => TypedResults.Ok(
                MapClient(result.Client ?? throw new InvalidOperationException(
                    "A successful client query did not provide a client."))),
            _ => throw new InvalidOperationException(
                "The client query returned an unknown status.")
        };
    }

    private static async Task<IResult> ListAsync(
        Guid organizationId,
        ClaimsPrincipal principal,
        ListClientsUseCase useCase,
        CancellationToken cancellationToken,
        int pageNumber = 1,
        int pageSize = ListClientsUseCase.DefaultPageSize)
    {
        if (!AuthenticatedUserId.TryGet(principal, out Guid userId))
        {
            return TypedResults.Unauthorized();
        }

        ListClientsResult result = await useCase.ExecuteAsync(
            userId,
            organizationId,
            pageNumber,
            pageSize,
            cancellationToken);

        if (result.Status == ListClientsResultStatus.AccessDenied)
        {
            return TypedResults.Forbid();
        }

        ClientResponse[] items = result.Items
            .Select(MapClient)
            .ToArray();

        return TypedResults.Ok(new ListClientsResponse(
            items,
            result.PageNumber,
            result.PageSize));
    }

    private static async Task<IResult> UpdateAsync(
        Guid organizationId,
        Guid clientId,
        UpdateClientRequest request,
        ClaimsPrincipal principal,
        UpdateClientUseCase useCase,
        CancellationToken cancellationToken)
    {
        if (!AuthenticatedUserId.TryGet(principal, out Guid userId))
        {
            return TypedResults.Unauthorized();
        }

        UpdateClientResult result = await useCase.ExecuteAsync(
            userId,
            organizationId,
            clientId,
            request.Name,
            cancellationToken);

        return result.Status switch
        {
            UpdateClientResultStatus.AccessDenied => TypedResults.Forbid(),
            UpdateClientResultStatus.NotFound => TypedResults.NotFound(),
            UpdateClientResultStatus.Succeeded => TypedResults.NoContent(),
            _ => throw new InvalidOperationException(
                "The client update returned an unknown status.")
        };
    }

    private static Task<IResult> DeactivateAsync(
        Guid organizationId,
        Guid clientId,
        ClaimsPrincipal principal,
        DeactivateClientUseCase useCase,
        CancellationToken cancellationToken)
    {
        return ChangeLifecycleAsync(
            principal,
            userId => ExecuteDeactivateAsync(
                useCase,
                userId,
                organizationId,
                clientId,
                cancellationToken));
    }

    private static Task<IResult> ReactivateAsync(
        Guid organizationId,
        Guid clientId,
        ClaimsPrincipal principal,
        ReactivateClientUseCase useCase,
        CancellationToken cancellationToken)
    {
        return ChangeLifecycleAsync(
            principal,
            userId => ExecuteReactivateAsync(
                useCase,
                userId,
                organizationId,
                clientId,
                cancellationToken));
    }

    private static async Task<IResult> ExecuteDeactivateAsync(
        DeactivateClientUseCase useCase,
        Guid userId,
        Guid organizationId,
        Guid clientId,
        CancellationToken cancellationToken)
    {
        DeactivateClientResult result = await useCase.ExecuteAsync(
            userId,
            organizationId,
            clientId,
            cancellationToken);

        return result.Status switch
        {
            DeactivateClientResultStatus.AccessDenied => TypedResults.Forbid(),
            DeactivateClientResultStatus.NotFound => TypedResults.NotFound(),
            DeactivateClientResultStatus.Succeeded => TypedResults.NoContent(),
            _ => throw new InvalidOperationException(
                "The client deactivation returned an unknown status.")
        };
    }

    private static async Task<IResult> ExecuteReactivateAsync(
        ReactivateClientUseCase useCase,
        Guid userId,
        Guid organizationId,
        Guid clientId,
        CancellationToken cancellationToken)
    {
        ReactivateClientResult result = await useCase.ExecuteAsync(
            userId,
            organizationId,
            clientId,
            cancellationToken);

        return result.Status switch
        {
            ReactivateClientResultStatus.AccessDenied => TypedResults.Forbid(),
            ReactivateClientResultStatus.NotFound => TypedResults.NotFound(),
            ReactivateClientResultStatus.Succeeded => TypedResults.NoContent(),
            _ => throw new InvalidOperationException(
                "The client reactivation returned an unknown status.")
        };
    }

    private static Task<IResult> ChangeLifecycleAsync(
        ClaimsPrincipal principal,
        Func<Guid, Task<IResult>> change)
    {
        if (!AuthenticatedUserId.TryGet(principal, out Guid userId))
        {
            return Task.FromResult<IResult>(TypedResults.Unauthorized());
        }

        return change(userId);
    }

    private static ClientResponse MapClient(ClientReadModel client)
    {
        return new ClientResponse(
            client.Id,
            client.Name,
            client.IsActive,
            client.CreatedAt);
    }
}
