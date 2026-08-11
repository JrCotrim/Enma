using Enma.Application.Abstractions;
using Enma.Application.Organizations.Create;
using Enma.Application.Users;
using Enma.Domain.Authentication;
using Enma.Domain.Clients;
using Enma.Domain.Organizations;
using Enma.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Enma.Infrastructure.Persistence;

public sealed class EnmaDbContext(DbContextOptions<EnmaDbContext> options)
    : DbContext(options), IUnitOfWork
{
    private const string OrganizationSlugUniqueConstraint = "ux_organizations_slug";
    private const string UserEmailUniqueConstraint = "ux_users_email";

    public DbSet<Organization> Organizations => Set<Organization>();

    public DbSet<Client> Clients => Set<Client>();

    public DbSet<User> Users => Set<User>();

    public DbSet<UserCredential> UserCredentials => Set<UserCredential>();

    public DbSet<AuthenticationSession> AuthenticationSessions =>
        Set<AuthenticationSession>();

    public DbSet<EmailVerificationChallenge> EmailVerificationChallenges =>
        Set<EmailVerificationChallenge>();

    public DbSet<EmailVerificationSendBudget> EmailVerificationSendBudgets =>
        Set<EmailVerificationSendBudget>();

    public DbSet<OrganizationMembership> OrganizationMemberships =>
        Set<OrganizationMembership>();

    async Task<int> IUnitOfWork.SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await base.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (
            exception.InnerException is PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation
            })
        {
            var postgresException = (PostgresException)exception.InnerException;

            switch (postgresException.ConstraintName)
            {
                case OrganizationSlugUniqueConstraint:
                    Organization? organization = exception.Entries
                        .Select(entry => entry.Entity)
                        .OfType<Organization>()
                        .FirstOrDefault();

                    if (organization is null)
                    {
                        throw;
                    }

                    throw new OrganizationSlugAlreadyExistsException(
                        organization.Slug,
                        exception);

                case UserEmailUniqueConstraint:
                    User? user = GetSingleConflictingUser(exception);

                    if (user is null)
                    {
                        throw;
                    }

                    throw new UserEmailAlreadyExistsException(
                        user.Email,
                        exception);

                default:
                    throw;
            }
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(EnmaDbContext).Assembly);
    }

    private static User? GetSingleConflictingUser(
        DbUpdateException exception)
    {
        User[] users = exception.Entries
            .Select(entry => entry.Entity)
            .OfType<User>()
            .Take(2)
            .ToArray();

        return users.Length == 1 ? users[0] : null;
    }
}
