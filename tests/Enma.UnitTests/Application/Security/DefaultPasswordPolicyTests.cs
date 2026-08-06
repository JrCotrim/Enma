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
    public void Validate_WithFourteenCharacters_ThrowsArgumentException()
    {
        var policy = new DefaultPasswordPolicy();

        var exception = Assert.Throws<ArgumentException>(
            () => policy.Validate(new string('x', 14)));

        Assert.Equal("password", exception.ParamName);
        Assert.Contains(PasswordPolicyErrors.PasswordTooShort, exception.Message);
    }

    [Fact]
    public void Validate_WithFifteenCharacters_Succeeds()
    {
        var policy = new DefaultPasswordPolicy();

        Exception? exception = Record.Exception(
            () => policy.Validate(new string('x', 15)));

        Assert.Null(exception);
    }

    [Fact]
    public void Validate_WithOneHundredTwentyEightCharacters_Succeeds()
    {
        var policy = new DefaultPasswordPolicy();

        Exception? exception = Record.Exception(
            () => policy.Validate(new string('x', 128)));

        Assert.Null(exception);
    }

    [Fact]
    public void Validate_WithOneHundredTwentyNineCharacters_ThrowsArgumentException()
    {
        var policy = new DefaultPasswordPolicy();

        var exception = Assert.Throws<ArgumentException>(
            () => policy.Validate(new string('x', 129)));

        Assert.Equal("password", exception.ParamName);
        Assert.Contains(PasswordPolicyErrors.PasswordTooLong, exception.Message);
    }

    [Fact]
    public void Validate_WithOnlyLowercaseCharactersAtValidLength_Succeeds()
    {
        var policy = new DefaultPasswordPolicy();

        Exception? exception = Record.Exception(
            () => policy.Validate("abcdefghijklmno"));

        Assert.Null(exception);
    }

    [Fact]
    public void Validate_WithOnlyUppercaseCharactersAtValidLength_Succeeds()
    {
        var policy = new DefaultPasswordPolicy();

        Exception? exception = Record.Exception(
            () => policy.Validate("ABCDEFGHIJKLMNO"));

        Assert.Null(exception);
    }

    [Fact]
    public void Validate_WithOnlyNumericCharactersAtValidLength_Succeeds()
    {
        var policy = new DefaultPasswordPolicy();

        Exception? exception = Record.Exception(
            () => policy.Validate("123456789012345"));

        Assert.Null(exception);
    }

    [Fact]
    public void Validate_WithOnlyNonWhitespaceSymbolsAtValidLength_Succeeds()
    {
        var policy = new DefaultPasswordPolicy();

        Exception? exception = Record.Exception(
            () => policy.Validate("!@#$%^&*()[]{}?"));

        Assert.Null(exception);
    }

    [Fact]
    public void Validate_WithLeadingAndTrailingSpacesAtValidRawLength_Succeeds()
    {
        var policy = new DefaultPasswordPolicy();

        Exception? exception = Record.Exception(
            () => policy.Validate(" synthetic!042 "));

        Assert.Null(exception);
    }
}
