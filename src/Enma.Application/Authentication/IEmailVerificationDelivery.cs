namespace Enma.Application.Authentication;

public interface IEmailVerificationDelivery
{
    Task<EmailVerificationDeliveryResult> DeliverAsync(
        string email,
        string rawToken,
        CancellationToken cancellationToken = default);

    Task<EmailVerificationDeliveryResult> DeliverAsync(
        string email,
        string rawToken,
        string invitationToken,
        CancellationToken cancellationToken = default)
    {
        return DeliverAsync(email, rawToken, cancellationToken);
    }
}
