namespace Enma.Application.Notifications;

public sealed class NotificationGenerationTransientException : Exception
{
    private const string DefaultMessage =
        "Notification generation encountered a transient persistence failure.";

    public NotificationGenerationTransientException(
        string classificationCode,
        Exception innerException)
        : base(DefaultMessage, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(classificationCode);
        ArgumentNullException.ThrowIfNull(innerException);
        ClassificationCode = classificationCode;
    }

    public string ClassificationCode { get; }
}
