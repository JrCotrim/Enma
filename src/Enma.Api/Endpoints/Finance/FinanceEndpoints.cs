using System.Globalization;
using System.Security.Claims;
using Enma.Api.Authentication;
using Enma.Api.Authorization;
using Enma.Api.Contracts.Finance;
using Enma.Api.Endpoints;
using Enma.Application.Finance.Create;

namespace Enma.Api.Endpoints.Finance;

public static class FinanceEndpoints
{
    private const string RoutePrefix =
        "/api/organizations/{organizationId:guid}/finance/payment-plans";

    public static IEndpointRouteBuilder MapFinanceEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        RouteGroupBuilder group = endpoints
            .MapGroup(RoutePrefix)
            .WithTags("Finance")
            .RequireAuthorization(EnmaAuthorizationPolicies.OrganizationAccess)
            .RequireNoStoreResponses();

        group.MapPost(string.Empty, CreateAsync)
            .WithName("CreatePaymentPlan")
            .WithSummary("Creates a payment plan for an active client.")
            .Accepts<CreatePaymentPlanRequest>("application/json")
            .Produces<CreatePaymentPlanResponse>(StatusCodes.Status201Created)
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
        CreatePaymentPlanRequest request,
        ClaimsPrincipal principal,
        CreatePaymentPlanUseCase useCase,
        CancellationToken cancellationToken)
    {
        if (!AuthenticatedUserId.TryGet(principal, out Guid userId))
        {
            return TypedResults.Unauthorized();
        }

        CreatePaymentPlanResult result = await useCase.ExecuteAsync(
            new CreatePaymentPlanCommand(
                userId,
                organizationId,
                request.ClientId,
                request.TotalAmount,
                request.InstallmentCount,
                request.FirstDueDate),
            cancellationToken);

        if (result.Status == CreatePaymentPlanResultStatus.Succeeded)
        {
            Guid paymentPlanId = result.PaymentPlanId
                ?? throw new InvalidOperationException(
                    "A successful payment plan creation did not provide an id.");
            string location = string.Create(
                CultureInfo.InvariantCulture,
                $"/api/organizations/{organizationId:D}/finance/" +
                $"payment-plans/{paymentPlanId:D}");

            return TypedResults.Created(
                location,
                new CreatePaymentPlanResponse(paymentPlanId));
        }

        return result.Status switch
        {
            CreatePaymentPlanResultStatus.AccessDenied => TypedResults.Forbid(),
            CreatePaymentPlanResultStatus.RelatedClientUnavailable =>
                TypedResults.NotFound(),
            _ => throw new InvalidOperationException(
                "Payment plan creation returned an unknown status.")
        };
    }
}
