namespace Enma.Application.Deadlines.Responsible;

public enum ChangeLegalDeadlineResponsibleResult
{
    AccessDenied = 0,
    NotFound = 1,
    InvalidInput = 2,
    RelatedResponsibleUnavailable = 3,
    Succeeded = 4
}
