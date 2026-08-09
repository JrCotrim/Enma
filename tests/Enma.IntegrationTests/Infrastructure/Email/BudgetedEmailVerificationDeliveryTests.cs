using Enma.Application.Authentication;
using Enma.Infrastructure.Email;

namespace Enma.IntegrationTests.Infrastructure.Email;

public sealed class BudgetedEmailVerificationDeliveryTests
{
    private const string Email = "recipient@example.test";
    private const string RawToken = "synthetic-raw-token";

    [Fact]
    public async Task DeliverAsync_BudgetDenied_ReturnsFailedWithoutCallingInnerDelivery()
    {
        var budget = new StubBudget(admitted: false);
        var innerDelivery = new RecordingDelivery(
            EmailVerificationDeliveryResult.Delivered);
        var logger = new MailKitEmailVerificationDeliveryTests
            .CapturingLogger<BudgetedEmailVerificationDelivery>();
        var delivery = new BudgetedEmailVerificationDelivery(
            budget,
            innerDelivery,
            logger);

        EmailVerificationDeliveryResult result = await delivery.DeliverAsync(
            Email,
            RawToken);

        Assert.Equal(EmailVerificationDeliveryResult.Failed, result);
        Assert.Equal(1, budget.AcquisitionCount);
        Assert.Equal(0, innerDelivery.CallCount);
        MailKitEmailVerificationDeliveryTests.LogEntry entry =
            Assert.Single(logger.Entries);
        Assert.Equal(2002, entry.EventId.Id);
        Assert.Equal(
            "Email verification delivery suppressed by send budget.",
            entry.Message);
        Assert.Null(entry.Exception);
        Assert.DoesNotContain(Email, entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(RawToken, entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeliverAsync_BudgetAdmittedAndInnerDelivered_ForwardsExactlyOnce()
    {
        var budget = new StubBudget(admitted: true);
        var innerDelivery = new RecordingDelivery(
            EmailVerificationDeliveryResult.Delivered);
        var logger = new MailKitEmailVerificationDeliveryTests
            .CapturingLogger<BudgetedEmailVerificationDelivery>();
        var delivery = new BudgetedEmailVerificationDelivery(
            budget,
            innerDelivery,
            logger);

        EmailVerificationDeliveryResult result = await delivery.DeliverAsync(
            Email,
            RawToken);

        Assert.Equal(EmailVerificationDeliveryResult.Delivered, result);
        Assert.Equal(1, budget.AcquisitionCount);
        Assert.Equal(1, innerDelivery.CallCount);
        Assert.Equal(Email, innerDelivery.LastEmail);
        Assert.Equal(RawToken, innerDelivery.LastRawToken);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task DeliverAsync_BudgetAdmittedAndInnerFailed_DoesNotRetryOrRefund()
    {
        var budget = new StubBudget(admitted: true);
        var innerDelivery = new RecordingDelivery(
            EmailVerificationDeliveryResult.Failed);
        var logger = new MailKitEmailVerificationDeliveryTests
            .CapturingLogger<BudgetedEmailVerificationDelivery>();
        var delivery = new BudgetedEmailVerificationDelivery(
            budget,
            innerDelivery,
            logger);

        EmailVerificationDeliveryResult result = await delivery.DeliverAsync(
            Email,
            RawToken);

        Assert.Equal(EmailVerificationDeliveryResult.Failed, result);
        Assert.Equal(1, budget.AcquisitionCount);
        Assert.Equal(1, innerDelivery.CallCount);
        Assert.Empty(logger.Entries);
    }

    private sealed class StubBudget(bool admitted) : IEmailVerificationSendBudget
    {
        public int AcquisitionCount { get; private set; }

        public Task<bool> TryAcquireAsync(
            string email,
            CancellationToken cancellationToken = default)
        {
            AcquisitionCount++;
            return Task.FromResult(admitted);
        }
    }

    private sealed class RecordingDelivery(
        EmailVerificationDeliveryResult result) : IEmailVerificationDelivery
    {
        public int CallCount { get; private set; }

        public string? LastEmail { get; private set; }

        public string? LastRawToken { get; private set; }

        public Task<EmailVerificationDeliveryResult> DeliverAsync(
            string email,
            string rawToken,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastEmail = email;
            LastRawToken = rawToken;
            return Task.FromResult(result);
        }
    }
}
