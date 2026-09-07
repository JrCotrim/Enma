namespace Enma.Application.Finance.List;

public sealed record ListPaymentPlansQuery(
    Guid UserId,
    Guid OrganizationId,
    Guid? ClientId = null,
    int PageNumber = 1,
    int PageSize = ListPaymentPlansUseCase.DefaultPageSize);
