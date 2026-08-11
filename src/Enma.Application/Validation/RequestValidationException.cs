namespace Enma.Application.Validation;

public sealed class RequestValidationException : Exception
{
    public RequestValidationException(string message)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
    }

    public RequestValidationException(
        string message,
        Exception innerException)
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ArgumentNullException.ThrowIfNull(innerException);
    }
}
