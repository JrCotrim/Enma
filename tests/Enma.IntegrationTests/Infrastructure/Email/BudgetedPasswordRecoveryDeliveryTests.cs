using Enma.Application.Authentication;
using Enma.Infrastructure.Email;
using Microsoft.Extensions.Logging.Abstractions;

namespace Enma.IntegrationTests.Infrastructure.Email;

public sealed class BudgetedPasswordRecoveryDeliveryTests
{
    [Fact]
    public async Task DeniedSharedDestinationBudgetSuppressesRecoveryDelivery()
    {
        var budget = new StubBudget { Admitted = false };
        var inner = new StubDelivery();
        var delivery = new BudgetedPasswordRecoveryDelivery(
            budget,
            inner,
            NullLogger<BudgetedPasswordRecoveryDelivery>.Instance);

        PasswordRecoveryDeliveryResult result = await delivery.DeliverAsync(
            "user@example.test",
            "synthetic-token");

        Assert.Equal(PasswordRecoveryDeliveryResult.Failed, result);
        Assert.Equal(1, budget.CallCount);
        Assert.Equal(0, inner.CallCount);
    }

    [Fact]
    public async Task InvalidRecipientFailsBeforeSmtpWithoutSensitiveException()
    {
        var logger = new MailKitEmailVerificationDeliveryTests
            .CapturingLogger<MailKitPasswordRecoveryDelivery>();
        EmailVerificationDeliveryOptions options =
            MailKitEmailVerificationDeliveryTests.CreateOptions(
                smtpPort: 1,
                includeCredentials: false);
        var wrapped = Microsoft.Extensions.Options.Options.Create(options);
        var delivery = new MailKitPasswordRecoveryDelivery(
            wrapped,
            new PasswordRecoveryLinkBuilder(wrapped),
            logger);

        PasswordRecoveryDeliveryResult result = await delivery.DeliverAsync(
            "not a mailbox",
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmno-_");

        Assert.Equal(PasswordRecoveryDeliveryResult.Failed, result);
        MailKitEmailVerificationDeliveryTests.LogEntry entry = Assert.Single(logger.Entries);
        Assert.Equal(2011, entry.EventId.Id);
        Assert.Null(entry.Exception);
        Assert.DoesNotContain("not a mailbox", entry.Message, StringComparison.Ordinal);
    }

    private sealed class StubBudget : IEmailVerificationSendBudget
    {
        public bool Admitted { get; set; }
        public int CallCount { get; private set; }

        public Task<bool> TryAcquireAsync(
            string email,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(Admitted);
        }
    }

    private sealed class StubDelivery : IPasswordRecoveryDelivery
    {
        public int CallCount { get; private set; }

        public Task<PasswordRecoveryDeliveryResult> DeliverAsync(
            string email,
            string rawToken,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(PasswordRecoveryDeliveryResult.Delivered);
        }
    }
}
