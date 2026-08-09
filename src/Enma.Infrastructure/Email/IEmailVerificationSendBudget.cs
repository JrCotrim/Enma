namespace Enma.Infrastructure.Email;

public interface IEmailVerificationSendBudget
{
    Task<bool> TryAcquireAsync(
        string email,
        CancellationToken cancellationToken = default);
}
