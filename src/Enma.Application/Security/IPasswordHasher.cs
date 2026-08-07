namespace Enma.Application.Security;

public interface IPasswordHasher
{
    string HashPassword(string password);

    PasswordVerificationResult VerifyHashedPassword(
        string passwordHash,
        string providedPassword);
}
