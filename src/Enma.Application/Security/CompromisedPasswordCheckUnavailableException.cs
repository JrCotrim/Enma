namespace Enma.Application.Security;

public sealed class CompromisedPasswordCheckUnavailableException : Exception
{
    private const string SafeMessage =
        "Password compromise screening is temporarily unavailable.";

    public CompromisedPasswordCheckUnavailableException()
        : base(SafeMessage)
    {
    }

    public CompromisedPasswordCheckUnavailableException(Exception innerException)
        : base(
            SafeMessage,
            innerException ?? throw new ArgumentNullException(nameof(innerException)))
    {
    }
}
