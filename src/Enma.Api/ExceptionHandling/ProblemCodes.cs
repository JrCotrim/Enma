namespace Enma.Api.ExceptionHandling;

// Stable, machine-readable values for the ProblemDetails "code" extension.
// Clients decide on these instead of on title/detail text, so they must not
// change once published.
internal static class ProblemCodes
{
    public const string RelatedResponsibleUnavailable =
        "related_responsible_unavailable";

    public const string RelatedAssigneeUnavailable =
        "related_assignee_unavailable";

    public const string DocumentUploadOutcomeUnknown =
        "document_upload_outcome_unknown";
}
