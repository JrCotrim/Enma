using Enma.Application.Authentication;
using Microsoft.Extensions.Logging;

namespace Enma.Infrastructure.Email;

public sealed class BudgetedPasswordRecoveryDelivery : IPasswordRecoveryDelivery
{
    private static readonly Action<ILogger, Exception?> LogSuppressed = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(2012, "PasswordRecoveryDeliverySuppressed"),
        "Password recovery delivery suppressed by send budget.");

    private readonly IEmailVerificationSendBudget sendBudget;
    private readonly IPasswordRecoveryDelivery innerDelivery;
    private readonly ILogger<BudgetedPasswordRecoveryDelivery> logger;

    public BudgetedPasswordRecoveryDelivery(
        IEmailVerificationSendBudget sendBudget,
        IPasswordRecoveryDelivery innerDelivery,
        ILogger<BudgetedPasswordRecoveryDelivery> logger)
    {
        this.sendBudget = sendBudget;
        this.innerDelivery = innerDelivery;
        this.logger = logger;
    }

    public async Task<PasswordRecoveryDeliveryResult> DeliverAsync(
        string email,
        string rawToken,
        CancellationToken cancellationToken = default)
    {
        if (!await sendBudget.TryAcquireAsync(email, cancellationToken))
        {
            LogSuppressed(logger, null);
            return PasswordRecoveryDeliveryResult.Failed;
        }

        return await innerDelivery.DeliverAsync(email, rawToken, cancellationToken);
    }
}
