using System.Net.Http.Headers;
using Enma.Application.Abstractions;
using Enma.Application.Authentication;
using Enma.Application.Organizations;
using Enma.Application.Security;
using Enma.Application.Users;
using Enma.Infrastructure.Email;
using Enma.Infrastructure.Persistence;
using Enma.Infrastructure.Persistence.Queries;
using Enma.Infrastructure.Persistence.Repositories;
using Enma.Infrastructure.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MicrosoftPasswordHasher = Microsoft.AspNetCore.Identity.IPasswordHasher<object>;

namespace Enma.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        string connectionString,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException(
                "The database connection string cannot be null, empty, or whitespace.",
                nameof(connectionString));
        }

        services.AddDbContext<EnmaDbContext>(
            options => options.UseNpgsql(connectionString));
        services.AddOptions<PasswordHasherOptions>();
        services
            .AddOptions<EmailVerificationDeliveryOptions>()
            .Bind(configuration.GetSection(
                EmailVerificationDeliveryOptions.SectionName));
        services
            .AddOptions<EmailVerificationSendBudgetOptions>()
            .Bind(configuration.GetSection(
                EmailVerificationSendBudgetOptions.SectionName));
        services.AddSingleton<
            IValidateOptions<EmailVerificationDeliveryOptions>,
            EmailVerificationDeliveryOptionsValidator>();
        services.AddSingleton<
            IValidateOptions<EmailVerificationSendBudgetOptions>,
            EmailVerificationSendBudgetOptionsValidator>();
        services.AddScoped<MicrosoftPasswordHasher, PasswordHasher<object>>();
        services.AddScoped<IPasswordHasher, AspNetCorePasswordHasher>();
        services.AddSingleton<ILoginDummyPasswordHashProvider>(serviceProvider =>
        {
            using IServiceScope initializationScope = serviceProvider.CreateScope();
            IPasswordHasher passwordHasher = initializationScope.ServiceProvider
                .GetRequiredService<IPasswordHasher>();

            return new LoginDummyPasswordHashProvider(passwordHasher);
        });
        services.AddSingleton<IPasswordPolicy, DefaultPasswordPolicy>();
        services.AddSingleton<
            IAuthenticationSessionHandleService,
            CryptographicAuthenticationSessionHandleService>();
        services.AddSingleton<
            IEmailVerificationTokenService,
            CryptographicEmailVerificationTokenService>();
        services.AddSingleton<EmailVerificationLinkBuilder>();
        services.AddSingleton<MailKitEmailVerificationDelivery>();
        services.AddScoped<
            IEmailVerificationSendBudget,
            PostgreSqlEmailVerificationSendBudget>();
        services.AddScoped<IEmailVerificationDelivery>(serviceProvider =>
            new BudgetedEmailVerificationDelivery(
                serviceProvider.GetRequiredService<IEmailVerificationSendBudget>(),
                serviceProvider.GetRequiredService<MailKitEmailVerificationDelivery>(),
                serviceProvider.GetRequiredService<
                    Microsoft.Extensions.Logging.ILogger<
                        BudgetedEmailVerificationDelivery>>()));
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
        services.AddScoped<
            IEmailVerificationChallengeRepository,
            EmailVerificationChallengeRepository>();
        services.AddScoped<IAuthenticationIdentityLookup, AuthenticationIdentityLookup>();
        services.AddScoped<
            IEmailVerificationUserLookup,
            EmailVerificationUserLookup>();
        services.AddScoped<
            IAuthenticationSessionRepository,
            AuthenticationSessionRepository>();
        services.AddScoped<
            IAuthenticationSessionIssuancePersistence,
            AuthenticationSessionIssuancePersistence>();
        services.AddScoped<
            IAuthenticationSessionRuntimePersistence,
            AuthenticationSessionRuntimePersistence>();
        services.AddScoped<
            IAuthenticationSessionRevocationPersistence,
            AuthenticationSessionRevocationPersistence>();
        services.AddScoped<
            IEmailVerificationChallengePersistence,
            EmailVerificationChallengePersistence>();
        services.AddScoped<
            IOrganizationMembershipRepository,
            OrganizationMembershipRepository>();
        services.AddScoped<IUnitOfWork>(
            serviceProvider => serviceProvider.GetRequiredService<EnmaDbContext>());
        services.AddScoped<LoginUseCase>();
        services.AddScoped<ValidateSessionUseCase>();
        services.AddScoped<RevokeSessionUseCase>();
        services.AddScoped<RequestEmailVerificationUseCase>();
        services.AddScoped<VerifyEmailUseCase>();

        return services;
    }
}
