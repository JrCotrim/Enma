using System.Globalization;
using System.Security.Claims;
using Enma.Api.Authentication;
using Enma.Api.Authorization;
using Enma.Api.Contracts.Finance;
using Enma.Api.Endpoints;
using Enma.Application.Finance;
using Enma.Application.Finance.Create;
using Enma.Application.Finance.GetById;
using Enma.Application.Finance.List;
using Enma.Application.Finance.MarkPaid;
using Enma.Application.Finance.Overview;

namespace Enma.Api.Endpoints.Finance;

public static class FinanceEndpoints
{
    private const string RoutePrefix =
        "/api/organizations/{organizationId:guid}/finance";

    public static IEndpointRouteBuilder MapFinanceEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        RouteGroupBuilder financeGroup = endpoints
            .MapGroup(RoutePrefix)
            .WithTags("Finance")
            .RequireAuthorization(EnmaAuthorizationPolicies.OrganizationAccess)
            .RequireNoStoreResponses();

        financeGroup.MapGet("/overview", GetOverviewAsync)
            .WithName("GetFinanceOverview")
            .WithSummary("Gets the financial overview for the contextual organization.")
            .Produces<FinanceOverviewResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        RouteGroupBuilder group = financeGroup.MapGroup("/payment-plans");

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

        group.MapGet(string.Empty, ListAsync)
            .WithName("ListPaymentPlans")
            .WithSummary("Lists payment plans in the contextual organization.")
            .Produces<ListPaymentPlansResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        group.MapGet("{paymentPlanId:guid}", GetAsync)
            .WithName("GetPaymentPlan")
            .WithSummary("Gets a payment plan in the contextual organization.")
            .Produces<PaymentPlanResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        group.MapPost(
                "{paymentPlanId:guid}/installments/{installmentId:guid}/mark-paid",
                MarkInstallmentPaidAsync)
            .WithName("MarkPaymentInstallmentPaid")
            .WithSummary("Marks a payment installment as paid.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .RequireEnmaAntiforgery();

        return endpoints;
    }

    private static async Task<IResult> GetOverviewAsync(
        Guid organizationId,
        ClaimsPrincipal principal,
        GetFinanceOverviewUseCase useCase,
        CancellationToken cancellationToken)
    {
        if (!AuthenticatedUserId.TryGet(principal, out Guid userId))
        {
            return TypedResults.Unauthorized();
        }

        GetFinanceOverviewResult result = await useCase.ExecuteAsync(
            new GetFinanceOverviewQuery(userId, organizationId),
            cancellationToken);

        return result.Status switch
        {
            GetFinanceOverviewResultStatus.AccessDenied => TypedResults.Forbid(),
            GetFinanceOverviewResultStatus.Succeeded => TypedResults.Ok(
                MapFinanceOverview(result.Overview ??
                    throw new InvalidOperationException(
                        "A successful finance overview query did not provide a read model."))),
            _ => throw new InvalidOperationException(
                "Finance overview query returned an unknown status.")
        };
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

    private static async Task<IResult> ListAsync(
        Guid organizationId,
        ClaimsPrincipal principal,
        ListPaymentPlansUseCase useCase,
        CancellationToken cancellationToken,
        Guid? clientId = null,
        int pageNumber = 1,
        int pageSize = ListPaymentPlansUseCase.DefaultPageSize)
    {
        if (!AuthenticatedUserId.TryGet(principal, out Guid userId))
        {
            return TypedResults.Unauthorized();
        }

        ListPaymentPlansResult result = await useCase.ExecuteAsync(
            new ListPaymentPlansQuery(
                userId,
                organizationId,
                clientId,
                pageNumber,
                pageSize),
            cancellationToken);

        if (result.Status == ListPaymentPlansResultStatus.AccessDenied)
        {
            return TypedResults.Forbid();
        }

        PaymentPlanSummaryResponse[] items = result.Items
            .Select(MapPaymentPlanSummary)
            .ToArray();

        return TypedResults.Ok(new ListPaymentPlansResponse(
            items,
            result.PageNumber,
            result.PageSize,
            result.HasNext));
    }

    private static async Task<IResult> GetAsync(
        Guid organizationId,
        Guid paymentPlanId,
        ClaimsPrincipal principal,
        GetPaymentPlanUseCase useCase,
        CancellationToken cancellationToken)
    {
        if (!AuthenticatedUserId.TryGet(principal, out Guid userId))
        {
            return TypedResults.Unauthorized();
        }

        GetPaymentPlanResult result = await useCase.ExecuteAsync(
            new GetPaymentPlanQuery(
                userId,
                organizationId,
                paymentPlanId),
            cancellationToken);

        return result.Status switch
        {
            GetPaymentPlanResultStatus.AccessDenied => TypedResults.Forbid(),
            GetPaymentPlanResultStatus.NotFound => TypedResults.NotFound(),
            GetPaymentPlanResultStatus.Succeeded => TypedResults.Ok(
                MapPaymentPlan(result.PaymentPlan ??
                    throw new InvalidOperationException(
                        "A successful payment plan query did not provide a plan."))),
            _ => throw new InvalidOperationException(
                "Payment plan query returned an unknown status.")
        };
    }

    private static async Task<IResult> MarkInstallmentPaidAsync(
        Guid organizationId,
        Guid paymentPlanId,
        Guid installmentId,
        ClaimsPrincipal principal,
        MarkPaymentInstallmentPaidUseCase useCase,
        CancellationToken cancellationToken)
    {
        if (!AuthenticatedUserId.TryGet(principal, out Guid userId))
        {
            return TypedResults.Unauthorized();
        }

        MarkPaymentInstallmentPaidResult result = await useCase.ExecuteAsync(
            new MarkPaymentInstallmentPaidCommand(
                userId,
                organizationId,
                paymentPlanId,
                installmentId),
            cancellationToken);

        return result switch
        {
            MarkPaymentInstallmentPaidResult.AccessDenied =>
                TypedResults.Forbid(),
            MarkPaymentInstallmentPaidResult.NotFound =>
                TypedResults.NotFound(),
            MarkPaymentInstallmentPaidResult.Succeeded =>
                TypedResults.NoContent(),
            _ => throw new InvalidOperationException(
                "Payment installment mark-paid returned an unknown result.")
        };
    }

    private static PaymentPlanSummaryResponse MapPaymentPlanSummary(
        PaymentPlanListItemReadModel paymentPlan)
    {
        return new PaymentPlanSummaryResponse(
            paymentPlan.Id,
            paymentPlan.ClientId,
            paymentPlan.ClientName,
            paymentPlan.TotalAmount,
            paymentPlan.InstallmentCount,
            paymentPlan.FirstDueDate,
            paymentPlan.CreatedAt,
            paymentPlan.OutstandingAmount,
            paymentPlan.OverdueInstallmentCount,
            paymentPlan.NextDueDate);
    }

    private static FinanceOverviewResponse MapFinanceOverview(
        FinanceOverviewReadModel overview)
    {
        return new FinanceOverviewResponse(
            overview.ReferenceDate,
            overview.TotalContractedAmount,
            overview.TotalReceivedAmount,
            overview.TotalOutstandingAmount,
            overview.OverdueAmount,
            overview.DueTodayAmount,
            overview.UpcomingAmount,
            overview.PaymentPlanCount,
            overview.OpenPaymentPlanCount,
            overview.PaidInstallmentCount,
            overview.OverdueInstallmentCount,
            overview.DueTodayInstallmentCount,
            overview.UpcomingInstallmentCount);
    }

    private static PaymentPlanResponse MapPaymentPlan(
        PaymentPlanDetailReadModel paymentPlan)
    {
        return new PaymentPlanResponse(
            paymentPlan.Id,
            paymentPlan.ClientId,
            paymentPlan.ClientName,
            paymentPlan.TotalAmount,
            paymentPlan.InstallmentCount,
            paymentPlan.FirstDueDate,
            paymentPlan.CreatedAt,
            paymentPlan.ReferenceDate,
            paymentPlan.Installments.Select(MapInstallment).ToArray());
    }

    private static PaymentInstallmentResponse MapInstallment(
        PaymentInstallmentReadModel installment)
    {
        return new PaymentInstallmentResponse(
            installment.Id,
            installment.SequenceNumber,
            installment.Amount,
            installment.DueDate,
            installment.PaidAt,
            installment.Status switch
            {
                PaymentInstallmentStatus.Paid =>
                    PaymentInstallmentStatusResponse.Paid,
                PaymentInstallmentStatus.Overdue =>
                    PaymentInstallmentStatusResponse.Overdue,
                PaymentInstallmentStatus.DueToday =>
                    PaymentInstallmentStatusResponse.DueToday,
                PaymentInstallmentStatus.Upcoming =>
                    PaymentInstallmentStatusResponse.Upcoming,
                _ => throw new InvalidOperationException(
                    "Payment installment has an unknown status.")
            });
    }
}
