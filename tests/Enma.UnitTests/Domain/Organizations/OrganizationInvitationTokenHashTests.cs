using System.Reflection;
using Enma.Domain.Organizations;

namespace Enma.UnitTests.Domain.Organizations;

public sealed class OrganizationInvitationTokenHashTests
{
    [Fact]
    public void Constructor_WithExactlyThirtyTwoBytes_DefensivelyCopiesValue()
    {
        byte[] source = CreateHashBytes(1);
        byte[] expected = (byte[])source.Clone();
        var hash = new OrganizationInvitationTokenHash(source);

        source[0]++;
        byte[] returned = hash.ToArray();
        returned[1]++;

        Assert.Equal(expected, hash.ToArray());
    }

    [Fact]
    public void Constructor_WithNullValue_Throws()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            new OrganizationInvitationTokenHash(null!));

        Assert.Equal("value", exception.ParamName);
        Assert.Contains(
            OrganizationInvitationErrors.TokenHashRequired,
            exception.Message);
    }

    [Theory]
    [InlineData(31)]
    [InlineData(33)]
    public void Constructor_WithInvalidLength_Throws(int length)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new OrganizationInvitationTokenHash(new byte[length]));

        Assert.Equal("value", exception.ParamName);
        Assert.Contains(
            OrganizationInvitationErrors.TokenHashLengthInvalid,
            exception.Message);
    }

    [Fact]
    public void Equality_WithIndependentEqualValues_IsValueBased()
    {
        var first = new OrganizationInvitationTokenHash(CreateHashBytes(1));
        var second = new OrganizationInvitationTokenHash(CreateHashBytes(1));
        var different = new OrganizationInvitationTokenHash(CreateHashBytes(2));

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.NotEqual(first, different);
    }

    [Fact]
    public void PublicSurface_ExposesNoStringOrMutableState()
    {
        var hash = new OrganizationInvitationTokenHash(CreateHashBytes(1));
        PropertyInfo[] publicProperties = typeof(OrganizationInvitationTokenHash)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public);

        Assert.Empty(publicProperties);
        Assert.Equal(typeof(OrganizationInvitationTokenHash).FullName, hash.ToString());
    }

    private static byte[] CreateHashBytes(byte seed)
    {
        return Enumerable.Range(seed, 32)
            .Select(value => (byte)value)
            .ToArray();
    }
}
