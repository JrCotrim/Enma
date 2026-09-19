namespace Enma.Application.Authentication;

public sealed class LoginResult
{
    private LoginResult(
        LoginResultStatus status,
        string? sessionHandle,
        Guid? userId)
    {
        Status = status;
        SessionHandle = sessionHandle;
        UserId = userId;
    }

    public LoginResultStatus Status { get; }

    public string? SessionHandle { get; }

    public Guid? UserId { get; }

    public static LoginResult InvalidCredentials { get; } = new(
        LoginResultStatus.InvalidCredentials,
        null,
        null);

    public static LoginResult Success(string sessionHandle, Guid userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionHandle);
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("The user id is required.", nameof(userId));
        }

        return new LoginResult(
            LoginResultStatus.Succeeded,
            sessionHandle,
            userId);
    }
}

public enum LoginResultStatus
{
    InvalidCredentials = 0,
    Succeeded = 1
}
