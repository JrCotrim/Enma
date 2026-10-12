using Enma.Infrastructure.Email;
using Microsoft.Extensions.Options;

namespace Enma.IntegrationTests.Infrastructure.Email;

public sealed class DevelopmentEmailVerificationDeliveryOptionsValidatorTests
{
    private readonly DevelopmentEmailVerificationDeliveryOptionsValidator
        validator = new();

    [Theory]
    [InlineData("http://localhost:5173/verify-email")]
    [InlineData("https://127.0.0.1:5173/verify-email")]
    [InlineData("http://[::1]:5173/verify-email")]
    [InlineData("https://enma-pilot.localhost:5443/verify-email")]
    [InlineData("https://ENMA-PILOT.LOCALHOST/verify-email")]
    public void Validate_LocalVerificationPageUrl_ReturnsSuccess(string value)
    {
        ValidateOptionsResult result = validator.Validate(
            null,
            new DevelopmentEmailVerificationDeliveryOptions
            {
                VerificationPageUrl = value
            });

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/verify-email")]
    [InlineData("ftp://localhost/verify-email")]
    [InlineData("http://app.example/verify-email")]
    [InlineData("https://localhost.example.test/verify-email")]
    [InlineData("https://enma-pilot.localhost.example.test/verify-email")]
    [InlineData("https://enma-localhost/verify-email")]
    [InlineData("https://10.0.0.5/verify-email")]
    [InlineData("http://localhost:5173/")]
    [InlineData("http://localhost:5173/verify-email/")]
    [InlineData("http://localhost:5173/verify-email?source=development")]
    [InlineData("http://localhost:5173/verify-email#token=existing")]
    [InlineData("http://user:password@localhost:5173/verify-email")]
    public void Validate_NonLocalOrMalformedVerificationPageUrl_ReturnsFailure(
        string value)
    {
        ValidateOptionsResult result = validator.Validate(
            null,
            new DevelopmentEmailVerificationDeliveryOptions
            {
                VerificationPageUrl = value
            });

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Failures ?? [],
            failure => failure.Contains(
                "VerificationPageUrl",
                StringComparison.Ordinal));
    }

    [Fact]
    public void SmtpHost_Default_IsIpv4LoopbackAndValid()
    {
        var options = new DevelopmentEmailVerificationDeliveryOptions();

        ValidateOptionsResult result = validator.Validate(null, options);

        Assert.Equal("127.0.0.1", options.SmtpHost);
        Assert.Equal(
            DevelopmentEmailVerificationDeliveryOptions.DefaultSmtpHost,
            options.SmtpHost);
        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("localhost")]
    [InlineData("2130706433")]
    [InlineData("mailpit")]
    [InlineData("enma-mailpit")]
    public void Validate_LoopbackOrSingleLabelSmtpHost_ReturnsSuccess(
        string value)
    {
        ValidateOptionsResult result = validator.Validate(
            null,
            new DevelopmentEmailVerificationDeliveryOptions
            {
                SmtpHost = value
            });

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("mailpit.")]
    [InlineData("mailpit.internal")]
    [InlineData("smtp.example.test")]
    [InlineData("enma-pilot.localhost")]
    [InlineData("10.0.0.5")]
    [InlineData("167772165")]
    [InlineData("fd00::5")]
    [InlineData("mail pit")]
    [InlineData("mailpit:1025")]
    public void Validate_DottedNonLoopbackOrMalformedSmtpHost_ReturnsFailure(
        string value)
    {
        ValidateOptionsResult result = validator.Validate(
            null,
            new DevelopmentEmailVerificationDeliveryOptions
            {
                SmtpHost = value
            });

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Failures ?? [],
            failure => failure.Contains("SmtpHost", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void Validate_InvalidSmtpPort_ReturnsFailure(int value)
    {
        ValidateOptionsResult result = validator.Validate(
            null,
            new DevelopmentEmailVerificationDeliveryOptions
            {
                SmtpPort = value
            });

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Failures ?? [],
            failure => failure.Contains("SmtpPort", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(61)]
    public void Validate_OutOfRangeSmtpTimeout_ReturnsFailure(int value)
    {
        ValidateOptionsResult result = validator.Validate(
            null,
            new DevelopmentEmailVerificationDeliveryOptions
            {
                SmtpTimeoutSeconds = value
            });

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Failures ?? [],
            failure => failure.Contains(
                "SmtpTimeoutSeconds",
                StringComparison.Ordinal));
    }
}
