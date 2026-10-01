using Enma.Infrastructure.Time;
using Microsoft.Extensions.Options;

namespace Enma.IntegrationTests.Infrastructure.Time;

public sealed class OperationalTimeZoneOptionsValidatorTests
{
    private readonly OperationalTimeZoneOptionsValidator validator = new();

    [Fact]
    public void Validate_DefaultOptions_UsesSaoPauloAndSucceeds()
    {
        var options = new OperationalTimeZoneOptions();

        ValidateOptionsResult result = validator.Validate(null, options);

        Assert.Equal("America/Sao_Paulo", options.TimeZoneId);
        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData("America/Sao_Paulo")]
    [InlineData("Asia/Tokyo")]
    [InlineData("Etc/UTC")]
    public void Validate_KnownIanaTimeZone_Succeeds(string timeZoneId)
    {
        ValidateOptionsResult result = validator.Validate(
            null,
            new OperationalTimeZoneOptions { TimeZoneId = timeZoneId });

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Invalid/Zone")]
    [InlineData(" America/Sao_Paulo")]
    [InlineData("America/Sao_Paulo ")]
    [InlineData("E. South America Standard Time")]
    public void Validate_UnknownBlankPaddedOrNonIanaTimeZone_Fails(
        string? timeZoneId)
    {
        ValidateOptionsResult result = validator.Validate(
            null,
            new OperationalTimeZoneOptions { TimeZoneId = timeZoneId! });

        Assert.True(result.Failed);
        string failure = Assert.Single(result.Failures!);
        Assert.Equal(
            "OperationalTimeZone:TimeZoneId must be a known IANA time zone ID.",
            failure);
    }
}
