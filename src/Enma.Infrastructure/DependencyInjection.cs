using Amazon.Runtime;
using Amazon.S3;
using System.Net.Http.Headers;
using Enma.Application.Abstractions;
using Enma.Application.Agenda;
using Enma.Application.Authorization;
using Enma.Application.Authentication;
using Enma.Application.CalendarEvents;
using Enma.Application.CalendarEvents.Assignment;
using Enma.Application.CalendarEvents.Create;
using Enma.Application.CalendarEvents.Delete;
using Enma.Application.CalendarEvents.GetById;
using Enma.Application.CalendarEvents.Update;
using Enma.Application.Clients;
using Enma.Application.Clients.Create;
using Enma.Application.Clients.Deactivate;
using Enma.Application.Clients.GetById;
using Enma.Application.Clients.List;
using Enma.Application.Clients.Lookup;
using Enma.Application.Clients.Reactivate;
using Enma.Application.Clients.Update;
using Enma.Application.Deadlines;
using Enma.Application.Deadlines.Complete;
using Enma.Application.Deadlines.Create;
using Enma.Application.Deadlines.GetById;
using Enma.Application.Deadlines.List;
using Enma.Application.Deadlines.Reopen;
using Enma.Application.Deadlines.Update;
using Enma.Application.Dashboard;
using Enma.Application.Documents.Inspection;
using Enma.Application.Documents;
using Enma.Application.Documents.Download;
using Enma.Application.Documents.GetById;
using Enma.Application.Documents.List;
using Enma.Application.Documents.Staging;
using Enma.Application.Documents.Storage;
using Enma.Application.Documents.Upload;
using Enma.Application.Notifications;
using Enma.Application.Notifications.List;
using Enma.Application.Notifications.MarkAllRead;
using Enma.Application.Notifications.MarkRead;
using Enma.Application.Organizations;
using Enma.Application.Organizations.CurrentUser;
using Enma.Application.Organizations.Members.List;
using Enma.Application.Organizations.Members.Lifecycle;
using Enma.Application.Organizations.Members.Lookup;
using Enma.Application.Organizations.Members.Role;
using Enma.Application.Processes;
using Enma.Application.Processes.Create;
using Enma.Application.Processes.GetById;
using Enma.Application.Processes.List;
using Enma.Application.Processes.Lookup;
using Enma.Application.Processes.Update;
using Enma.Application.Security;
using Enma.Application.Tasks;
using Enma.Application.Tasks.Assignment;
using Enma.Application.Tasks.Complete;
using Enma.Application.Tasks.Create;
using Enma.Application.Tasks.GetById;
using Enma.Application.Tasks.List;
using Enma.Application.Tasks.Reopen;
using Enma.Application.Tasks.Update;
using Enma.Application.Users;
using Enma.Infrastructure.Documents.Inspection;
using Enma.Infrastructure.Documents.Staging;
using Enma.Infrastructure.Documents.Storage;
using Enma.Infrastructure.Documents.Upload;
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
        return AddInfrastructure(
            services,
            connectionString,
            configuration,
            isDevelopment: false);
    }

    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        string connectionString,
        IConfiguration configuration,
        bool isDevelopment)
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
        if (isDevelopment)
        {
            services
                .AddOptions<DevelopmentEmailVerificationDeliveryOptions>()
                .Bind(configuration.GetSection(
                    DevelopmentEmailVerificationDeliveryOptions.SectionName));
            services.AddSingleton<
                IValidateOptions<DevelopmentEmailVerificationDeliveryOptions>,
                DevelopmentEmailVerificationDeliveryOptionsValidator>();
        }
        else
        {
            services
                .AddOptions<EmailVerificationDeliveryOptions>()
                .Bind(configuration.GetSection(
                    EmailVerificationDeliveryOptions.SectionName));
            services.AddSingleton<
                IValidateOptions<EmailVerificationDeliveryOptions>,
                EmailVerificationDeliveryOptionsValidator>();
        }
        services
            .AddOptions<EmailVerificationSendBudgetOptions>()
            .Bind(configuration.GetSection(
                EmailVerificationSendBudgetOptions.SectionName));
        services
            .AddOptions<DocumentStorageOptions>()
            .Bind(configuration.GetSection(
                DocumentStorageOptions.SectionName));
        services.AddSingleton<
            IValidateOptions<EmailVerificationSendBudgetOptions>,
            EmailVerificationSendBudgetOptionsValidator>();
        services.AddSingleton<
            IValidateOptions<DocumentStorageOptions>,
            DocumentStorageOptionsValidator>();
        services.AddSingleton<IAmazonS3>(serviceProvider =>
        {
            DocumentStorageOptions options = serviceProvider
                .GetRequiredService<IOptions<DocumentStorageOptions>>()
                .Value;

            var credentials = new BasicAWSCredentials(
                options.AccessKey,
                options.SecretKey);
            var clientConfiguration = new AmazonS3Config
            {
                ServiceURL = options.ServiceUrl,
                ForcePathStyle = options.ForcePathStyle,
                AuthenticationRegion = options.Region
            };

            return new AmazonS3Client(credentials, clientConfiguration);
        });
        services.AddSingleton<
            ILegalDocumentStorage,
            S3LegalDocumentStorage>();
        services.AddSingleton<
            ILegalDocumentContentStager,
            TempFileLegalDocumentContentStager>();
        services.AddSingleton<
            ILegalDocumentContentInspector,
            LegalDocumentContentInspector>();
        services.AddScoped<
            ILegalDocumentMetadataUploadTransaction,
            LegalDocumentMetadataUploadTransaction>();
        services.AddScoped<
            ILegalDocumentUploadPersistence,
            LegalDocumentUploadPersistence>();
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
        if (isDevelopment)
        {
            services.AddSingleton<DevelopmentEmailVerificationDelivery>();
        }
        else
        {
            services.AddSingleton<EmailVerificationLinkBuilder>();
            services.AddSingleton<MailKitEmailVerificationDelivery>();
        }
        services.AddScoped<
            IEmailVerificationSendBudget,
            PostgreSqlEmailVerificationSendBudget>();
        services.AddScoped<IEmailVerificationDelivery>(serviceProvider =>
            new BudgetedEmailVerificationDelivery(
                serviceProvider.GetRequiredService<IEmailVerificationSendBudget>(),
                isDevelopment
                    ? serviceProvider.GetRequiredService<
                        DevelopmentEmailVerificationDelivery>()
                    : serviceProvider.GetRequiredService<
                        MailKitEmailVerificationDelivery>(),
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
            IClientOrganizationOwnershipLookup,
            ClientOrganizationOwnershipLookup>();
        services.AddScoped<
            IProcessOrganizationOwnershipLookup,
            ProcessOrganizationOwnershipLookup>();
        services.AddScoped<
            IDeadlineOrganizationOwnershipLookup,
            DeadlineOrganizationOwnershipLookup>();
        services.AddScoped<
            IActiveClientInOrganizationLookup,
            ActiveClientInOrganizationLookup>();
        services.AddScoped<IClientCreationPersistence, ClientCreationPersistence>();
        services.AddScoped<IClientMutationPersistence, ClientMutationPersistence>();
        services.AddScoped<IClientReadQueries, ClientReadQueries>();
        services.AddScoped<IActiveClientLookupQueries, ActiveClientLookupQueries>();
        services.AddScoped<
            ILegalProcessCreationPersistence,
            LegalProcessCreationPersistence>();
        services.AddScoped<
            ILegalProcessMutationPersistence,
            LegalProcessMutationPersistence>();
        services.AddScoped<ILegalProcessReadQueries, LegalProcessReadQueries>();
        services.AddScoped<
            ILegalProcessLookupQueries,
            LegalProcessLookupQueries>();
        services.AddScoped<
            ILegalDeadlineCreationPersistence,
            LegalDeadlineCreationPersistence>();
        services.AddScoped<
            ILegalDeadlineMutationPersistence,
            LegalDeadlineMutationPersistence>();
        services.AddScoped<ILegalDeadlineReadQueries, LegalDeadlineReadQueries>();
        services.AddScoped<
            ILegalTaskCreationPersistence,
            LegalTaskCreationPersistence>();
        services.AddScoped<
            ILegalTaskMutationPersistence,
            LegalTaskMutationPersistence>();
        services.AddScoped<ILegalTaskReadQueries, LegalTaskReadQueries>();
        services.AddScoped<
            ICalendarEventCreationPersistence,
            CalendarEventCreationPersistence>();
        services.AddScoped<
            ICalendarEventMutationPersistence,
            CalendarEventMutationPersistence>();
        services.AddScoped<ICalendarEventReadQueries, CalendarEventReadQueries>();
        services.AddScoped<IAgendaReadQueries, AgendaReadQueries>();
        services.AddScoped<IDashboardReadQueries, DashboardReadQueries>();
        services.AddScoped<INotificationReadQueries, NotificationReadQueries>();
        services.AddScoped<
            INotificationMutationPersistence,
            NotificationMutationPersistence>();
        services.AddScoped<
            INotificationGenerationPersistence,
            NotificationGenerationPersistence>();
        services.AddScoped<ILegalDocumentReadQueries, LegalDocumentReadQueries>();
        services.AddScoped<
            ILegalDocumentContentReadQueries,
            LegalDocumentContentReadQueries>();
        services.AddScoped<IOrganizationAccessLookup, OrganizationAccessLookup>();
        services.AddScoped<
            ICurrentUserOrganizationQueries,
            CurrentUserOrganizationQueries>();
        services.AddScoped<
            IOrganizationMemberLookupQueries,
            OrganizationMemberLookupQueries>();
        services.AddScoped<
            IOrganizationMemberAdministrationQueries,
            OrganizationMemberAdministrationQueries>();
        services.AddScoped<
            IOrganizationMemberRoleMutationPersistence,
            OrganizationMemberRoleMutationPersistence>();
        services.AddScoped<
            IOrganizationMemberLifecycleMutationPersistence,
            OrganizationMemberLifecycleMutationPersistence>();
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
        services.AddScoped<OrganizationAccessAuthorization>();
        services.AddScoped<OrganizationAdministrationAuthorization>();
        services.AddScoped<GetCurrentUserOrganizationsUseCase>();
        services.AddScoped<ListOrganizationMembersUseCase>();
        services.AddScoped<SearchActiveOrganizationMembersUseCase>();
        services.AddScoped<ChangeOrganizationMemberRoleUseCase>();
        services.AddScoped<OrganizationMemberLifecycleUseCase>();
        services.AddScoped<ClientAccessAuthorization>();
        services.AddScoped<ClientActionAuthorization>();
        services.AddScoped<ProcessAccessAuthorization>();
        services.AddScoped<ProcessActionAuthorization>();
        services.AddScoped<DeadlineAccessAuthorization>();
        services.AddScoped<DeadlineActionAuthorization>();
        services.AddScoped<LegalTaskViewAuthorization>();
        services.AddScoped<LegalTaskMutationAuthorization>();
        services.AddScoped<CalendarEventAccessAuthorization>();
        services.AddScoped<CalendarEventActionAuthorization>();
        services.AddScoped<GetAgendaUseCase>();
        services.AddScoped<GetDashboardUseCase>();
        services.AddScoped<ListNotificationsUseCase>();
        services.AddScoped<MarkNotificationAsReadUseCase>();
        services.AddScoped<MarkAllNotificationsAsReadUseCase>();
        services.AddScoped<GenerateNotificationsUseCase>();
        services.AddScoped<LegalDocumentReadAuthorization>();
        services.AddScoped<CreateClientUseCase>();
        services.AddScoped<DeactivateClientUseCase>();
        services.AddScoped<GetClientUseCase>();
        services.AddScoped<ListClientsUseCase>();
        services.AddScoped<SearchActiveClientsUseCase>();
        services.AddScoped<ReactivateClientUseCase>();
        services.AddScoped<UpdateClientUseCase>();
        services.AddScoped<CreateLegalProcessUseCase>();
        services.AddScoped<GetLegalProcessUseCase>();
        services.AddScoped<ListLegalProcessesUseCase>();
        services.AddScoped<SearchLegalProcessesUseCase>();
        services.AddScoped<UpdateLegalProcessUseCase>();
        services.AddScoped<CreateLegalDeadlineUseCase>();
        services.AddScoped<CompleteLegalDeadlineUseCase>();
        services.AddScoped<GetLegalDeadlineUseCase>();
        services.AddScoped<ListLegalDeadlinesUseCase>();
        services.AddScoped<ReopenLegalDeadlineUseCase>();
        services.AddScoped<UpdateLegalDeadlineUseCase>();
        services.AddScoped<CreateLegalTaskUseCase>();
        services.AddScoped<ChangeLegalTaskAssigneeUseCase>();
        services.AddScoped<CompleteLegalTaskUseCase>();
        services.AddScoped<GetLegalTaskUseCase>();
        services.AddScoped<ListLegalTasksUseCase>();
        services.AddScoped<ReopenLegalTaskUseCase>();
        services.AddScoped<UpdateLegalTaskUseCase>();
        services.AddScoped<CreateCalendarEventUseCase>();
        services.AddScoped<GetCalendarEventUseCase>();
        services.AddScoped<UpdateCalendarEventUseCase>();
        services.AddScoped<ChangeCalendarEventAssigneeUseCase>();
        services.AddScoped<DeleteCalendarEventUseCase>();
        services.AddScoped<UploadLegalDocumentUseCase>();
        services.AddScoped<DownloadLegalDocumentUseCase>();
        services.AddScoped<GetLegalDocumentUseCase>();
        services.AddScoped<ListLegalDocumentsUseCase>();

        return services;
    }
}
