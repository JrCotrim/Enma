using Enma.Infrastructure.Email;
using Microsoft.Extensions.Options;

namespace Enma.IntegrationTests.Infrastructure.Email;

public sealed class EmailVerificationLinkBuilderTests
{
    private const string SyntheticToken =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmno-_";

    [Fact]
    public void Build_ValidToken_PlacesExactTokenOnlyInFragment()
    {
        var builder = CreateBuilder("https://app.example/verify-email");

        Uri result = builder.Build(SyntheticToken);

        Assert.Equal(Uri.UriSchemeHttps, result.Scheme);
        Assert.Equal("app.example", result.Host);
        Assert.Equal("/verify-email", result.AbsolutePath);
        Assert.Equal(string.Empty, result.Query);
        Assert.Equal($"#token={SyntheticToken}", result.Fragment);
        Assert.Equal(
            $"https://app.example/verify-email#token={SyntheticToken}",
            result.AbsoluteUri);
        Assert.DoesNotContain(SyntheticToken, result.GetLeftPart(UriPartial.Path));
        Assert.DoesNotContain(SyntheticToken, result.PathAndQuery);
        Assert.Contains(SyntheticToken, result.Fragment);
    }

    [Fact]
    public void Build_DeploymentPrefix_PreservesConfiguredPathAndTokenAlphabet()
    {
        var builder = CreateBuilder("https://app.example/enma/verify-email");

        Uri result = builder.Build(SyntheticToken);

        Assert.Equal("/enma/verify-email", result.AbsolutePath);
        Assert.Equal(string.Empty, result.Query);
        Assert.Equal($"#token={SyntheticToken}", result.Fragment);
        Assert.Contains("-_", result.Fragment, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmno+=")]
    public void Build_MalformedToken_ThrowsWithoutEchoingValue(string rawToken)
    {
        var builder = CreateBuilder("https://app.example/verify-email");

        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => builder.Build(rawToken));

        if (rawToken.Length > 0)
        {
            Assert.DoesNotContain(rawToken, exception.Message, StringComparison.Ordinal);
        }
    }

    private static EmailVerificationLinkBuilder CreateBuilder(string pageUrl)
    {
        return new EmailVerificationLinkBuilder(
            Options.Create(new EmailVerificationDeliveryOptions
            {
                VerificationPageUrl = pageUrl
            }));
    }
}
