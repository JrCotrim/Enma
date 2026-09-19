using Enma.Application.Security;

namespace Enma.UnitTests.Application.Security;

public sealed class DefaultPasswordPolicyTests
{
    [Fact]
    public void Validate_WithNullPassword_ThrowsArgumentNullException()
    {
        var policy = new DefaultPasswordPolicy();

        var exception = Assert.Throws<ArgumentNullException>(
            () => policy.Validate(null!));

        Assert.Equal("password", exception.ParamName);
    }

    [Fact]
    public void Validate_WithEmptyPassword_ThrowsArgumentException()
    {
        var policy = new DefaultPasswordPolicy();

        var exception = Assert.Throws<ArgumentException>(
            () => policy.Validate(string.Empty));

        Assert.Equal("password", exception.ParamName);
        Assert.Contains(PasswordPolicyErrors.PasswordRequired, exception.Message);
    }

    [Fact]
    public void Validate_WithWhitespaceOnlyPassword_ThrowsArgumentException()
    {
        var policy = new DefaultPasswordPolicy();

        var exception = Assert.Throws<ArgumentException>(
            () => policy.Validate(" \t \t "));

        Assert.Equal("password", exception.ParamName);
        Assert.Contains(PasswordPolicyErrors.PasswordRequired, exception.Message);
    }

    [Fact]
    public void Validate_WithSevenCharacters_ThrowsArgumentException()
    {
        var policy = new DefaultPasswordPolicy();

        var exception = Assert.Throws<ArgumentException>(
            () => policy.Validate("Abcdef1"));

        Assert.Equal("password", exception.ParamName);
        Assert.Contains(PasswordPolicyErrors.PasswordTooShort, exception.Message);
    }

    [Fact]
    public void Validate_WithEightCharactersAndRequiredComposition_Succeeds()
    {
        var policy = new DefaultPasswordPolicy();

        Exception? exception = Record.Exception(
            () => policy.Validate("Abcdefg1"));

        Assert.Null(exception);
    }

    [Fact]
    public void Validate_WithOneHundredTwentyEightCharacters_Succeeds()
    {
        var policy = new DefaultPasswordPolicy();

        Exception? exception = Record.Exception(
            () => policy.Validate($"Aa1{new string('x', 125)}"));

        Assert.Null(exception);
    }

    [Fact]
    public void Validate_WithOneHundredTwentyNineCharacters_ThrowsArgumentException()
    {
        var policy = new DefaultPasswordPolicy();

        var exception = Assert.Throws<ArgumentException>(
            () => policy.Validate($"Aa1{new string('x', 126)}"));

        Assert.Equal("password", exception.ParamName);
        Assert.Contains(PasswordPolicyErrors.PasswordTooLong, exception.Message);
    }

    [Fact]
    public void Validate_WithoutUppercaseLetter_ThrowsArgumentException()
    {
        var policy = new DefaultPasswordPolicy();

        var exception = Assert.Throws<ArgumentException>(
            () => policy.Validate("abcdefg1"));

        Assert.Equal("password", exception.ParamName);
        Assert.Contains(PasswordPolicyErrors.PasswordMissingUppercase, exception.Message);
    }

    [Fact]
    public void Validate_WithoutLowercaseLetter_ThrowsArgumentException()
    {
        var policy = new DefaultPasswordPolicy();

        var exception = Assert.Throws<ArgumentException>(
            () => policy.Validate("ABCDEFG1"));

        Assert.Equal("password", exception.ParamName);
        Assert.Contains(PasswordPolicyErrors.PasswordMissingLowercase, exception.Message);
    }

    [Fact]
    public void Validate_WithoutNumber_ThrowsArgumentException()
    {
        var policy = new DefaultPasswordPolicy();

        var exception = Assert.Throws<ArgumentException>(
            () => policy.Validate("Abcdefgh"));

        Assert.Equal("password", exception.ParamName);
        Assert.Contains(PasswordPolicyErrors.PasswordMissingNumber, exception.Message);
    }

    [Fact]
    public void Validate_WithoutSymbol_Succeeds()
    {
        var policy = new DefaultPasswordPolicy();

        Exception? exception = Record.Exception(
            () => policy.Validate("Abcdefg1"));

        Assert.Null(exception);
    }

    [Fact]
    public void Validate_WithLeadingAndTrailingSpacesAtValidRawLength_Succeeds()
    {
        var policy = new DefaultPasswordPolicy();

        Exception? exception = Record.Exception(
            () => policy.Validate(" Synthetic042 "));

        Assert.Null(exception);
    }
}
