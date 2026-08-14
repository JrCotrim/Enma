namespace Enma.Application.Tasks.Update;

public enum UpdateLegalTaskResult
{
    AccessDenied = 0,
    NotFound = 1,
    RelatedProcessUnavailable = 2,
    InvalidInput = 3,
    Conflict = 4,
    Succeeded = 5
}
