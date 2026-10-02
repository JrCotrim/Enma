using Enma.Domain.Clients;

namespace Enma.UnitTests.Domain.Clients;

public sealed class BrazilianTaxIdTests
{
    [Theory]
    [InlineData("123.456.789-09", "12345678909")]
    [InlineData("12345678909", "12345678909")]
    [InlineData(" 123 456 789 09 ", "12345678909")]
    public void TryNormalizeCpf_WithValidCpf_ReturnsDigits(
        string value,
        string expected)
    {
        bool valid = BrazilianTaxId.TryNormalizeCpf(value, out string? normalized);

        Assert.True(valid);
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("111.111.111-11")]
    [InlineData("123.456.789-00")]
    [InlineData("1234567890")]
    [InlineData("123456789012")]
    [InlineData("123.456.789/09")]
    [InlineData("123.456.789-0A")]
    public void TryNormalizeCpf_WithInvalidCpf_ReturnsFalse(string value)
    {
        bool valid = BrazilianTaxId.TryNormalizeCpf(value, out string? normalized);

        Assert.False(valid);
        Assert.Null(normalized);
    }

    [Theory]
    [InlineData("11.222.333/0001-81", "11222333000181")]
    [InlineData("11222333000181", "11222333000181")]
    [InlineData("  11 222 333 0001 81  ", "11222333000181")]
    public void TryNormalizeCnpj_WithValidNumericCnpj_ReturnsNormalizedValue(
        string value,
        string expected)
    {
        bool valid = BrazilianTaxId.TryNormalizeCnpj(value, out string? normalized);

        Assert.True(valid);
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("11.222.333/0001-82")]
    [InlineData("11.222.333/0001-91")]
    public void TryNormalizeCnpj_WithWrongNumericCheckDigits_ReturnsFalse(
        string value)
    {
        Assert.False(BrazilianTaxId.TryNormalizeCnpj(value, out string? normalized));
        Assert.Null(normalized);
    }

    [Fact]
    public void TryNormalizeCnpj_WithPublishedAlphanumericExample_ReturnsNormalizedValue()
    {
        bool valid = BrazilianTaxId.TryNormalizeCnpj(
            "12.ABC.345/01DE-35",
            out string? normalized);

        Assert.True(valid);
        Assert.Equal("12ABC34501DE35", normalized);
    }

    [Theory]
    [InlineData("12.abc.345/01de-35")]
    [InlineData("12.AbC.345/01dE-35")]
    public void TryNormalizeCnpj_WithLowercaseLetters_ReturnsUppercaseValue(
        string value)
    {
        bool valid = BrazilianTaxId.TryNormalizeCnpj(value, out string? normalized);

        Assert.True(valid);
        Assert.Equal("12ABC34501DE35", normalized);
    }

    [Theory]
    [InlineData("IOQF1234000199", "IOQF1234000199")]
    [InlineData("iOqF.1234/0001-99", "IOQF1234000199")]
    [InlineData("QF0I9O8A000103", "QF0I9O8A000103")]
    public void TryNormalizeCnpj_WithLettersIOQFAndMatchingCheckDigits_Accepts(
        string value,
        string expected)
    {
        bool valid = BrazilianTaxId.TryNormalizeCnpj(value, out string? normalized);

        Assert.True(valid);
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("I0QF1234000199")]
    [InlineData("11222333OOO181")]
    public void TryNormalizeCnpj_NeverTreatsLetterOAsDigitZero(string value)
    {
        Assert.False(BrazilianTaxId.TryNormalizeCnpj(value, out _));
    }

    [Theory]
    [InlineData("12ABC34501DE36")]
    [InlineData("12ABC34501DF35")]
    public void TryNormalizeCnpj_WithWrongAlphanumericCheckDigits_ReturnsFalse(
        string value)
    {
        Assert.False(BrazilianTaxId.TryNormalizeCnpj(value, out _));
    }

    [Theory]
    [InlineData("11.222.333/0001-8!")]
    [InlineData("11_222_333_0001_81")]
    [InlineData("11.222.333\\0001-81")]
    [InlineData("11222333\t000181")]
    [InlineData("12ABC34501D\u00C935")]
    [InlineData("\u0131OQF1234000199")]
    [InlineData("\uFF11\uFF11222333000181")]
    public void TryNormalizeCnpj_WithInvalidCharacters_ReturnsFalse(string value)
    {
        Assert.False(BrazilianTaxId.TryNormalizeCnpj(value, out string? normalized));
        Assert.Null(normalized);
    }

    [Theory]
    [InlineData("12ABC34501DE3A")]
    [InlineData("12ABC34501DEA5")]
    public void TryNormalizeCnpj_WithLetterInCheckDigitPositions_ReturnsFalse(
        string value)
    {
        Assert.False(BrazilianTaxId.TryNormalizeCnpj(value, out _));
    }

    [Theory]
    [InlineData("00000000000000")]
    [InlineData("00.000.000/0000-00")]
    [InlineData("11111111111111")]
    public void TryNormalizeCnpj_WithAllCharactersEqual_ReturnsFalse(string value)
    {
        Assert.False(BrazilianTaxId.TryNormalizeCnpj(value, out _));
    }

    [Theory]
    [InlineData("1122233300018")]
    [InlineData("112223330001810")]
    [InlineData("12ABC34501DE")]
    [InlineData(".-/ ")]
    public void TryNormalizeCnpj_WithWrongLength_ReturnsFalse(string value)
    {
        Assert.False(BrazilianTaxId.TryNormalizeCnpj(value, out _));
    }
}
