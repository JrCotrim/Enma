namespace Enma.Application.Authentication;

public sealed class LoginResult
{
    private LoginResult(
        LoginResultStatus status,
        string? sessionHandle)
    {
        Status = status;
        SessionHandle = sessionHandle;
    }

    public LoginResultStatus Status { get; }

    public string? SessionHandle { get; }

    public static LoginResult InvalidCredentials { get; } = new(
        LoginResultStatus.InvalidCredentials,
        null);

    public static LoginResult Success(string sessionHandle)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionHandle);

        return new LoginResult(
            LoginResultStatus.Succeeded,
            sessionHandle);
    }
}

public enum LoginResultStatus
{
    InvalidCredentials = 0,
    Succeeded = 1
}
