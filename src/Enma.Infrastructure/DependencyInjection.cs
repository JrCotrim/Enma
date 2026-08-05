using Enma.Application.Abstractions;
using Enma.Application.Organizations;
using Enma.Infrastructure.Persistence;
using Enma.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

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
        services.AddScoped<IOrganizationRepository, OrganizationRepository>();
        services.AddScoped<IUnitOfWork>(
            serviceProvider => serviceProvider.GetRequiredService<EnmaDbContext>());

        return services;
    }
}
