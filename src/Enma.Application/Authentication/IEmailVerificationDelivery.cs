namespace Enma.Application.Authentication;

public interface IEmailVerificationDelivery
{
    Task<EmailVerificationDeliveryResult> DeliverAsync(
        string email,
        string rawToken,
        CancellationToken cancellationToken = default);
}
