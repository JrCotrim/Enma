using Enma.Domain.Users;

namespace Enma.Application.Users;

public interface IUserCredentialRepository
{
    Task<UserCredential?> GetByUserIdAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    Task AddAsync(
        UserCredential credential,
        CancellationToken cancellationToken = default);
}
