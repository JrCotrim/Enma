using Enma.Domain.Users;

namespace Enma.Application.Security;

public interface IPasswordHasher
{
    string HashPassword(
        User user,
        string password);

    PasswordVerificationResult VerifyHashedPassword(
        User user,
        string passwordHash,
        string providedPassword);
}
