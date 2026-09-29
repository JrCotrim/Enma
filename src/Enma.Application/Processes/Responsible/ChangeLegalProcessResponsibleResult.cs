namespace Enma.Application.Processes.Responsible;

public enum ChangeLegalProcessResponsibleResult
{
    AccessDenied = 0,
    NotFound = 1,
    InvalidInput = 2,
    RelatedResponsibleUnavailable = 3,
    Succeeded = 4
}
