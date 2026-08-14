namespace Enma.Application.Tasks.Assignment;

public enum ChangeLegalTaskAssigneeResult
{
    AccessDenied = 0,
    NotFound = 1,
    RelatedAssigneeUnavailable = 2,
    InvalidInput = 3,
    Conflict = 4,
    Succeeded = 5
}
