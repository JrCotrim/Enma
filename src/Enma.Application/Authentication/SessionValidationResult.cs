namespace Enma.Application.Authentication;

public sealed class SessionValidationResult
{
    private SessionValidationResult(
        SessionValidationResultStatus status,
        Guid? userId)
    {
        Status = status;
        UserId = userId;
    }

    public SessionValidationResultStatus Status { get; }

    public Guid? UserId { get; }

    public static SessionValidationResult Unauthenticated { get; } = new(
        SessionValidationResultStatus.Unauthenticated,
        null);

    public static SessionValidationResult Authenticated(Guid userId)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException(
                "Authenticated user id cannot be empty.",
                nameof(userId));
        }

        return new SessionValidationResult(
            SessionValidationResultStatus.Authenticated,
            userId);
    }
}

public enum SessionValidationResultStatus
{
    Unauthenticated = 0,
    Authenticated = 1
}
