using System.Data;
using Enma.Application.Onboarding;
using Enma.Domain.Organizations;
using Enma.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Enma.Infrastructure.Persistence;

public sealed class InitialOrganizationPersistence(
    DbContextOptions<EnmaDbContext> dbContextOptions,
    TimeProvider timeProvider) : IInitialOrganizationPersistence
{
    public async Task<InitialOrganizationResult> CreateAsync(
        Guid userId,
        string provider,
        string providerSubject,
        string organizationName,
        string organizationSlug,
        CancellationToken cancellationToken = default)
    {
        Organization organization;
        try
        {
            organization = new Organization(
                organizationName,
                organizationSlug,
                timeProvider.GetUtcNow());
        }
        catch (ArgumentException)
        {
            return new(InitialOrganizationStatus.Invalid);
        }

        await using var dbContext = new EnmaDbContext(dbContextOptions);
        await using IDbContextTransaction transaction =
            await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);
        User? user = await dbContext.Users
            .FromSqlInterpolated(
                $"SELECT * FROM users WHERE id = {userId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        DateTimeOffset now = timeProvider.GetUtcNow();
        bool hasMembership = await dbContext.OrganizationMemberships
            .AsNoTracking()
            .AnyAsync(
                membership => membership.UserId == userId,
                cancellationToken);
        bool hasMatchingExternalIdentity = await dbContext.ExternalIdentities
            .AsNoTracking()
            .AnyAsync(
                identity => identity.UserId == userId &&
                    identity.Provider == provider &&
                    identity.ProviderSubject == providerSubject,
                cancellationToken);

        if (user is null ||
            !user.IsActive ||
            user.EmailVerifiedAt is null ||
            hasMembership ||
            !hasMatchingExternalIdentity)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(InitialOrganizationStatus.Ineligible);
        }

        dbContext.Organizations.Add(organization);
        dbContext.OrganizationMemberships.Add(new OrganizationMembership(
            organization.Id,
            userId,
            OrganizationRole.Owner,
            now));

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(
                InitialOrganizationStatus.Succeeded,
                organization.Id);
        }
        catch (DbUpdateException exception) when (IsSlugConflict(exception))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(InitialOrganizationStatus.SlugConflict);
        }
    }

    private static bool IsSlugConflict(Exception exception)
    {
        for (Exception? current = exception;
             current is not null;
             current = current.InnerException)
        {
            if (current is PostgresException
                {
                    SqlState: PostgresErrorCodes.UniqueViolation,
                    ConstraintName: "ux_organizations_slug"
                })
            {
                return true;
            }
        }

        return false;
    }
}
