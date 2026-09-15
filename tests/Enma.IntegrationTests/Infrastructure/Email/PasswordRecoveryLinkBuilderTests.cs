using Enma.Infrastructure.Email;

namespace Enma.IntegrationTests.Infrastructure.Email;

public sealed class PasswordRecoveryLinkBuilderTests
{
    private const string Token = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmno-_";

    [Fact]
    public void BuildPlacesTokenOnlyInFragment()
    {
        var builder = new PasswordRecoveryLinkBuilder(
            new Uri("https://app.example/reset-password"));

        Uri result = builder.Build(Token);

        Assert.Equal("https://app.example/reset-password", result.GetLeftPart(UriPartial.Path));
        Assert.Equal(string.Empty, result.Query);
        Assert.Equal($"#token={Token}", result.Fragment);
        Assert.DoesNotContain(Token, result.PathAndQuery, StringComparison.Ordinal);
    }
}
