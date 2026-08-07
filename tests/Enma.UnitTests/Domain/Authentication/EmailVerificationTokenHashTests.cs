using System.Reflection;
using Enma.Domain.Authentication;

namespace Enma.UnitTests.Domain.Authentication;

public sealed class EmailVerificationTokenHashTests
{
    [Fact]
    public void Constructor_WithValidValue_ConstructsSuccessfully()
    {
        byte[] source = CreateHashBytes(1);

        var value = new EmailVerificationTokenHash(source);

        Assert.Equal(source, value.ToArray());
    }

    [Fact]
    public void Constructor_WithNullValue_ThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            new EmailVerificationTokenHash(null!));

        Assert.Equal("value", exception.ParamName);
        Assert.Contains(
            EmailVerificationChallengeErrors.TokenHashRequired,
            exception.Message);
    }

    [Fact]
    public void Constructor_WithThirtyOneBytes_ThrowsArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new EmailVerificationTokenHash(new byte[31]));

        Assert.Equal("value", exception.ParamName);
        Assert.Contains(
            EmailVerificationChallengeErrors.TokenHashLengthInvalid,
            exception.Message);
    }

    [Fact]
    public void Constructor_WithThirtyThreeBytes_ThrowsArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new EmailVerificationTokenHash(new byte[33]));

        Assert.Equal("value", exception.ParamName);
        Assert.Contains(
            EmailVerificationChallengeErrors.TokenHashLengthInvalid,
            exception.Message);
    }

    [Fact]
    public void Constructor_WhenSourceChanges_PreservesOriginalValue()
    {
        byte[] source = CreateHashBytes(1);
        byte[] expected = (byte[])source.Clone();
        var value = new EmailVerificationTokenHash(source);

        source[0]++;

        Assert.Equal(expected, value.ToArray());
    }

    [Fact]
    public void ToArray_WhenReturnedArrayChanges_PreservesOriginalValue()
    {
        byte[] expected = CreateHashBytes(1);
        var value = new EmailVerificationTokenHash(expected);

        byte[] returnedValue = value.ToArray();
        returnedValue[0]++;

        Assert.Equal(expected, value.ToArray());
    }

    [Fact]
    public void Equals_WithIndependentEqualValues_ReturnsTrue()
    {
        var value = new EmailVerificationTokenHash(CreateHashBytes(1));
        var equalValue = new EmailVerificationTokenHash(CreateHashBytes(1));

        Assert.Equal(value, equalValue);
        Assert.True(value.Equals(equalValue));
    }

    [Fact]
    public void Equals_WithUnequalValues_ReturnsFalse()
    {
        var value = new EmailVerificationTokenHash(CreateHashBytes(1));
        var unequalValue = new EmailVerificationTokenHash(CreateHashBytes(2));

        Assert.NotEqual(value, unequalValue);
        Assert.False(value.Equals(unequalValue));
    }

    [Fact]
    public void GetHashCode_WithEqualValues_ReturnsEqualHashCodes()
    {
        var value = new EmailVerificationTokenHash(CreateHashBytes(1));
        var equalValue = new EmailVerificationTokenHash(CreateHashBytes(1));

        Assert.Equal(value.GetHashCode(), equalValue.GetHashCode());
    }

    [Fact]
    public void PublicSurface_WhenInspected_ExposesNoMutableOrStringState()
    {
        var value = new EmailVerificationTokenHash(CreateHashBytes(1));
        PropertyInfo[] publicProperties = typeof(EmailVerificationTokenHash)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public);

        Assert.Empty(publicProperties);
        Assert.Equal(typeof(EmailVerificationTokenHash).FullName, value.ToString());
    }

    private static byte[] CreateHashBytes(byte seed)
    {
        return Enumerable.Range(seed, 32)
            .Select(value => (byte)value)
            .ToArray();
    }
}
