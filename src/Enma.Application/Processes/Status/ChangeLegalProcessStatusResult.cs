namespace Enma.Application.Processes.Status;

public enum ChangeLegalProcessStatusResult
{
    AccessDenied = 0,
    NotFound = 1,
    StatusTransitionNotAllowed = 2,
    CurrentResponsibleUnavailable = 3,
    Succeeded = 4
}
