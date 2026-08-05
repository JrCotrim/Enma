using Enma.Application.Abstractions;
using Enma.Application.Organizations;
using Enma.Application.Security;
using Enma.Application.Users;
using Enma.Domain.Users;
using Enma.Infrastructure.Persistence;
using Enma.Infrastructure.Persistence.Repositories;
using Enma.Infrastructure.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MicrosoftPasswordHasher = Microsoft.AspNetCore.Identity.IPasswordHasher<Enma.Domain.Users.User>;

namespace Enma.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException(
                "The database connection string cannot be null, empty, or whitespace.",
                nameof(connectionString));
        }

        services.AddDbContext<EnmaDbContext>(
            options => options.UseNpgsql(connectionString));
        services.AddOptions<PasswordHasherOptions>();
        services.AddScoped<MicrosoftPasswordHasher, PasswordHasher<User>>();
        services.AddScoped<IPasswordHasher, AspNetCorePasswordHasher>();
        services.AddScoped<IOrganizationRepository, OrganizationRepository>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IUserCredentialRepository, UserCredentialRepository>();
        services.AddScoped<
            IOrganizationMembershipRepository,
            OrganizationMembershipRepository>();
        services.AddScoped<IUnitOfWork>(
            serviceProvider => serviceProvider.GetRequiredService<EnmaDbContext>());

        return services;
    }
}
