using Enma.Infrastructure.Email;
using Microsoft.Extensions.Options;

namespace Enma.IntegrationTests.Infrastructure.Email;

public sealed class EmailVerificationSendBudgetOptionsValidatorTests
{
    private readonly EmailVerificationSendBudgetOptionsValidator validator = new();

    [Fact]
    public void Defaults_ProductionConfiguration_UsesSafeLimits()
    {
        var options = new EmailVerificationSendBudgetOptions();

        Assert.Equal(100, options.GlobalHourlyLimit);
        Assert.Equal(5, options.DestinationDailyLimit);
        Assert.True(validator.Validate(null, options).Succeeded);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1_000_000)]
    public void Validate_GlobalLimitAtAllowedBoundary_ReturnsSuccess(int value)
    {
        var options = new EmailVerificationSendBudgetOptions
        {
            GlobalHourlyLimit = value,
            DestinationDailyLimit = 5
        };

        Assert.True(validator.Validate(null, options).Succeeded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1_000_001)]
    public void Validate_GlobalLimitOutsideAllowedRange_ReturnsFailure(int value)
    {
        var options = new EmailVerificationSendBudgetOptions
        {
            GlobalHourlyLimit = value,
            DestinationDailyLimit = 5
        };

        ValidateOptionsResult result = validator.Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Failures ?? [],
            failure => failure.Contains("GlobalHourlyLimit", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10_000)]
    public void Validate_DestinationLimitAtAllowedBoundary_ReturnsSuccess(int value)
    {
        var options = new EmailVerificationSendBudgetOptions
        {
            GlobalHourlyLimit = 100,
            DestinationDailyLimit = value
        };

        Assert.True(validator.Validate(null, options).Succeeded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10_001)]
    public void Validate_DestinationLimitOutsideAllowedRange_ReturnsFailure(int value)
    {
        var options = new EmailVerificationSendBudgetOptions
        {
            GlobalHourlyLimit = 100,
            DestinationDailyLimit = value
        };

        ValidateOptionsResult result = validator.Validate(null, options);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Failures ?? [],
            failure => failure.Contains(
                "DestinationDailyLimit",
                StringComparison.Ordinal));
    }
}
