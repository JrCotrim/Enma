using Enma.Application.Authentication;
using Microsoft.Extensions.Logging;

namespace Enma.Infrastructure.Email;

public sealed class BudgetedEmailVerificationDelivery
    : IEmailVerificationDelivery
{
    private static readonly Action<ILogger, Exception?> LogSuppressed =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(2002, "EmailVerificationDeliverySuppressed"),
            "Email verification delivery suppressed by send budget.");

    private readonly IEmailVerificationSendBudget sendBudget;
    private readonly IEmailVerificationDelivery innerDelivery;
    private readonly ILogger<BudgetedEmailVerificationDelivery> logger;

    public BudgetedEmailVerificationDelivery(
        IEmailVerificationSendBudget sendBudget,
        IEmailVerificationDelivery innerDelivery,
        ILogger<BudgetedEmailVerificationDelivery> logger)
    {
        ArgumentNullException.ThrowIfNull(sendBudget);
        ArgumentNullException.ThrowIfNull(innerDelivery);
        ArgumentNullException.ThrowIfNull(logger);

        this.sendBudget = sendBudget;
        this.innerDelivery = innerDelivery;
        this.logger = logger;
    }

    public async Task<EmailVerificationDeliveryResult> DeliverAsync(
        string email,
        string rawToken,
        CancellationToken cancellationToken = default)
    {
        bool admitted = await sendBudget.TryAcquireAsync(email, cancellationToken);

        if (!admitted)
        {
            LogSuppressed(logger, null);
            return EmailVerificationDeliveryResult.Failed;
        }

        return await innerDelivery.DeliverAsync(
            email,
            rawToken,
            cancellationToken);
    }
}
