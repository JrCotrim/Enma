namespace Enma.Application.Authentication;

public interface IEmailVerificationUserLookup
{
    Task<Guid?> FindUserIdByEmailAsync(
        string normalizedEmail,
        CancellationToken cancellationToken = default);
}
