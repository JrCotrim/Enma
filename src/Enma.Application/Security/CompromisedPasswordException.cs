namespace Enma.Application.Security;

public sealed class CompromisedPasswordException : Exception
{
    public CompromisedPasswordException()
        : base("The provided password has appeared in a known data breach and cannot be used.")
    {
    }
}
