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
}
