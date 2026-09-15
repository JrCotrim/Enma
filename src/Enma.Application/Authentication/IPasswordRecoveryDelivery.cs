namespace Enma.Application.Authentication;

public interface IPasswordRecoveryDelivery
{
    Task<PasswordRecoveryDeliveryResult> DeliverAsync(
        string email,
        string rawToken,
        CancellationToken cancellationToken = default);
}
