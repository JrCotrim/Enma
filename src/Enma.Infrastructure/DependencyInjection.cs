using System.Net.Http.Headers;
using Enma.Application.Abstractions;
using Enma.Application.Authentication;
using Enma.Application.Organizations;
using Enma.Application.Security;
using Enma.Application.Users;
using Enma.Infrastructure.Persistence;
using Enma.Infrastructure.Persistence.Queries;
using Enma.Infrastructure.Persistence.Repositories;
using Enma.Infrastructure.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MicrosoftPasswordHasher = Microsoft.AspNetCore.Identity.IPasswordHasher<object>;

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
        services.AddScoped<MicrosoftPasswordHasher, PasswordHasher<object>>();
        services.AddScoped<IPasswordHasher, AspNetCorePasswordHasher>();
        services.AddSingleton<IPasswordPolicy, DefaultPasswordPolicy>();
        services.AddTransient<PwnedPasswordsTelemetryHandler>();
        services
            .AddHttpClient<
                ICompromisedPasswordChecker,
                PwnedPasswordsCompromisedPasswordChecker>(httpClient =>
            {
                httpClient.BaseAddress = new Uri("https://api.pwnedpasswords.com/");
                httpClient.Timeout = TimeSpan.FromSeconds(5);
                httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ENMA/1.0");
                httpClient.DefaultRequestHeaders.Add("Add-Padding", "true");
                httpClient.DefaultRequestHeaders.Accept.Add(
                    new MediaTypeWithQualityHeaderValue("text/plain"));
            })
            .RemoveAllLoggers()
            .AddHttpMessageHandler<PwnedPasswordsTelemetryHandler>()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false
            });
        services.AddScoped<IOrganizationRepository, OrganizationRepository>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IUserCredentialRepository, UserCredentialRepository>();
        services.AddScoped<IAuthenticationIdentityLookup, AuthenticationIdentityLookup>();
        services.AddScoped<
            IAuthenticationSessionRepository,
            AuthenticationSessionRepository>();
        services.AddScoped<
            IOrganizationMembershipRepository,
            OrganizationMembershipRepository>();
        services.AddScoped<IUnitOfWork>(
            serviceProvider => serviceProvider.GetRequiredService<EnmaDbContext>());

        return services;
    }
}
