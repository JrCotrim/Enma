using Enma.Application.Authentication;
using Enma.Domain.Users;
using Microsoft.EntityFrameworkCore;

namespace Enma.Infrastructure.Persistence.Queries;

public sealed class AuthenticationIdentityLookup : IAuthenticationIdentityLookup
{
    private readonly EnmaDbContext _dbContext;

    public AuthenticationIdentityLookup(EnmaDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    public Task<AuthenticationIdentity?> FindByNormalizedEmailAsync(
        string normalizedEmail,
        AuthenticationIdentityLoadMode loadMode,
        CancellationToken cancellationToken = default)
    {
        string canonicalEmail = User.NormalizeEmail(normalizedEmail);

        if (!string.Equals(
            normalizedEmail,
            canonicalEmail,
            StringComparison.Ordinal))
        {
            throw new ArgumentException(
                AuthenticationIdentityErrors.EmailMustBeNormalized,
                nameof(normalizedEmail));
        }

        bool isReadOnly = loadMode switch
        {
            AuthenticationIdentityLoadMode.ReadOnly => true,
            AuthenticationIdentityLoadMode.ForCredentialUpgrade => false,
            _ => throw new ArgumentOutOfRangeException(nameof(loadMode))
        };

        IQueryable<AuthenticationIdentity> query =
            from user in _dbContext.Users
            join credential in _dbContext.UserCredentials
                on user.Id equals credential.UserId into userCredentials
            from credential in userCredentials.DefaultIfEmpty()
            where user.Email == normalizedEmail
            select new AuthenticationIdentity(
                user.Id,
                user.Email,
                user.IsActive,
                user.EmailVerifiedAt,
                credential);

        if (isReadOnly)
        {
            query = query.AsNoTracking();
        }

        return query.SingleOrDefaultAsync(cancellationToken);
    }
}
