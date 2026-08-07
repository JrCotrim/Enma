using Enma.Domain.Authentication;

namespace Enma.UnitTests.Domain.Authentication;

public sealed class AuthenticationSessionSecretHashTests
{
    [Fact]
    public void Constructor_WithExactlyThirtyTwoBytes_CreatesDefensiveValue()
    {
        byte[] source = CreateHashBytes(1);
        byte[] expected = (byte[])source.Clone();
        var value = new AuthenticationSessionSecretHash(source);
        var equalValue = new AuthenticationSessionSecretHash(expected);
        var unequalValue = new AuthenticationSessionSecretHash(CreateHashBytes(2));

        Assert.True(expected.SequenceEqual(value.ToArray()));
        Assert.Equal(value, equalValue);
        Assert.Equal(value.GetHashCode(), equalValue.GetHashCode());
        Assert.NotEqual(value, unequalValue);

        source[0] ^= byte.MaxValue;
        Assert.True(expected.SequenceEqual(value.ToArray()));

        byte[] returnedValue = value.ToArray();
        returnedValue[1] ^= byte.MaxValue;
        Assert.True(expected.SequenceEqual(value.ToArray()));
    }

    [Fact]
    public void Constructor_WithNullValue_Throws()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            new AuthenticationSessionSecretHash(null!));

        Assert.Equal("value", exception.ParamName);
        Assert.Contains(
            AuthenticationSessionErrors.SecretHashRequired,
            exception.Message);
    }

    [Fact]
    public void Constructor_WithThirtyOneBytes_ThrowsArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new AuthenticationSessionSecretHash(new byte[31]));

        Assert.Equal("value", exception.ParamName);
        Assert.Contains(
            AuthenticationSessionErrors.SecretHashLengthInvalid,
            exception.Message);
    }

    [Fact]
    public void Constructor_WithThirtyThreeBytes_ThrowsArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new AuthenticationSessionSecretHash(new byte[33]));

        Assert.Equal("value", exception.ParamName);
        Assert.Contains(
            AuthenticationSessionErrors.SecretHashLengthInvalid,
            exception.Message);
    }

    private static byte[] CreateHashBytes(byte seed)
    {
        return Enumerable.Range(0, 32)
            .Select(index => (byte)(seed + index))
            .ToArray();
    }
}
