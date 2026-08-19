using Enma.Domain.Documents;

namespace Enma.UnitTests.Domain.Documents;

public sealed class LegalDocumentContentHashTests
{
    [Fact]
    public void Constructor_WithThirtyTwoBytes_StoresDefensiveCopy()
    {
        byte[] source = CreateHash(1);
        var hash = new LegalDocumentContentHash(source);

        source[0] = 255;

        Assert.Equal(1, hash.ToArray()[0]);
    }

    [Fact]
    public void ToArray_CalledTwice_ReturnsIndependentCopies()
    {
        var hash = new LegalDocumentContentHash(
            CreateHash(7));

        byte[] first = hash.ToArray();
        byte[] second = hash.ToArray();

        Assert.NotSame(first, second);

        first[0] = 255;

        Assert.Equal(7, second[0]);
        Assert.Equal(7, hash.ToArray()[0]);
    }

    [Fact]
    public void Constructor_WithNull_ThrowsArgumentNullException()
    {
        var exception =
            Assert.Throws<ArgumentNullException>(
                () => new LegalDocumentContentHash(null!));

        Assert.Equal("value", exception.ParamName);
        Assert.Contains(
            LegalDocumentErrors.ContentHashRequired,
            exception.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(33)]
    public void Constructor_WithInvalidLength_ThrowsArgumentException(
        int length)
    {
        var exception =
            Assert.Throws<ArgumentException>(
                () => new LegalDocumentContentHash(
                    new byte[length]));

        Assert.Equal("value", exception.ParamName);
        Assert.Contains(
            LegalDocumentErrors.ContentHashLengthInvalid,
            exception.Message);
    }

    [Fact]
    public void Equals_WithSameBytes_ReturnsTrue()
    {
        byte[] value = CreateHash(3);
        var first = new LegalDocumentContentHash(value);
        var second = new LegalDocumentContentHash(value);

        Assert.Equal(first, second);
        Assert.Equal(
            first.GetHashCode(),
            second.GetHashCode());
    }

    [Fact]
    public void Equals_WithDifferentBytes_ReturnsFalse()
    {
        var first =
            new LegalDocumentContentHash(CreateHash(3));
        var second =
            new LegalDocumentContentHash(CreateHash(4));

        Assert.NotEqual(first, second);
    }

    private static byte[] CreateHash(byte firstByte)
    {
        byte[] hash = new byte[32];
        hash[0] = firstByte;
        return hash;
    }
}
