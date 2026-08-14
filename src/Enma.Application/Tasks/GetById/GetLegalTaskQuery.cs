namespace Enma.Application.Tasks.GetById;

public sealed record GetLegalTaskQuery(
    Guid UserId,
    Guid OrganizationId,
    Guid LegalTaskId);
