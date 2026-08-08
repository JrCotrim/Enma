using Enma.Infrastructure.Email;
using MailKit.Security;
using Microsoft.Extensions.Options;

namespace Enma.IntegrationTests.Infrastructure.Email;

public sealed class EmailVerificationDeliveryOptionsValidatorTests
{
    private readonly EmailVerificationDeliveryOptionsValidator validator = new();

    [Theory]
    [InlineData(SecureSocketOptions.StartTls)]
    [InlineData(SecureSocketOptions.SslOnConnect)]
    public void Validate_SecureCompleteConfiguration_ReturnsSuccess(
        SecureSocketOptions smtpSecurity)
    {
        EmailVerificationDeliveryOptions options = CreateOptions(
            smtpSecurity: smtpSecurity);

        ValidateOptionsResult result = validator.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/verify-email")]
    [InlineData("http://app.example/verify-email")]
    [InlineData("https://app.example/verify-email?source=email")]
    [InlineData("https://app.example/verify-email#existing")]
    [InlineData("https://user:password@app.example/verify-email")]
    public void Validate_InvalidVerificationPageUrl_ReturnsFailure(string value)
    {
        EmailVerificationDeliveryOptions options = CreateOptions(
            verificationPageUrl: value);

        ValidateOptionsResult result = validator.Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Failures ?? [],
            failure => failure.Contains("VerificationPageUrl", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-mailbox")]
    [InlineData("ENMA <no-reply@example.test>")]
    public void Validate_InvalidSenderAddress_ReturnsFailure(string value)
    {
        EmailVerificationDeliveryOptions options = CreateOptions(senderAddress: value);

        ValidateOptionsResult result = validator.Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Failures ?? [],
            failure => failure.Contains("SenderAddress", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("smtp://smtp.example.test")]
    [InlineData("smtp.example.test/path")]
    public void Validate_InvalidSmtpHost_ReturnsFailure(string value)
    {
        EmailVerificationDeliveryOptions options = CreateOptions(smtpHost: value);

        ValidateOptionsResult result = validator.Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Failures ?? [],
            failure => failure.Contains("SmtpHost", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65_536)]
    public void Validate_OutOfRangeSmtpPort_ReturnsFailure(int value)
    {
        EmailVerificationDeliveryOptions options = CreateOptions(smtpPort: value);

        ValidateOptionsResult result = validator.Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Failures ?? [],
            failure => failure.Contains("SmtpPort", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(SecureSocketOptions.None)]
    [InlineData(SecureSocketOptions.Auto)]
    [InlineData(SecureSocketOptions.StartTlsWhenAvailable)]
    public void Validate_InsecureOrOpportunisticSmtpMode_ReturnsFailure(
        SecureSocketOptions smtpSecurity)
    {
        EmailVerificationDeliveryOptions options = CreateOptions(
            smtpSecurity: smtpSecurity);

        ValidateOptionsResult result = validator.Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Failures ?? [],
            failure => failure.Contains("SmtpSecurity", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_MissingSenderName_ReturnsFailure()
    {
        ValidateOptionsResult result = validator.Validate(
            null,
            CreateOptions(senderName: string.Empty));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Failures ?? [],
            failure => failure.Contains("SenderName", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_MissingSmtpUsername_ReturnsFailure()
    {
        ValidateOptionsResult result = validator.Validate(
            null,
            CreateOptions(smtpUsername: string.Empty));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Failures ?? [],
            failure => failure.Contains("SmtpUsername", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_MissingSmtpPassword_ReturnsFailure()
    {
        ValidateOptionsResult result = validator.Validate(
            null,
            CreateOptions(smtpPassword: string.Empty));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Failures ?? [],
            failure => failure.Contains("SmtpPassword", StringComparison.Ordinal));
    }

    private static EmailVerificationDeliveryOptions CreateOptions(
        string verificationPageUrl = "https://app.example/verify-email",
        string senderName = "ENMA",
        string senderAddress = "no-reply@example.test",
        string smtpHost = "smtp.example.test",
        int smtpPort = 587,
        SecureSocketOptions smtpSecurity = SecureSocketOptions.StartTls,
        string smtpUsername = "smtp-user",
        string smtpPassword = "synthetic-smtp-password")
    {
        return new EmailVerificationDeliveryOptions
        {
            VerificationPageUrl = verificationPageUrl,
            SenderName = senderName,
            SenderAddress = senderAddress,
            SmtpHost = smtpHost,
            SmtpPort = smtpPort,
            SmtpSecurity = smtpSecurity,
            SmtpUsername = smtpUsername,
            SmtpPassword = smtpPassword
        };
    }
}
