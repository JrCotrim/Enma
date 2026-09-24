using System.Threading.RateLimiting;
using System.Text.Json.Serialization;
using Enma.Api.Authentication;
using Enma.Api.Authorization;
using Enma.Api.Deployment;
using Enma.Api.Documents;
using Enma.Api.Endpoints;
using Enma.Api.Endpoints.Agenda;
using Enma.Api.Endpoints.Auditing;
using Enma.Api.Endpoints.Authentication;
using Enma.Api.Endpoints.CalendarEvents;
using Enma.Api.Endpoints.Clients;
using Enma.Api.Endpoints.Deadlines;
using Enma.Api.Endpoints.Dashboard;
using Enma.Api.Endpoints.Documents;
using Enma.Api.Endpoints.Finance;
using Enma.Api.Endpoints.Onboarding;
using Enma.Api.Endpoints.Notifications;
using Enma.Api.Endpoints.Organizations;
using Enma.Api.Endpoints.Processes;
using Enma.Api.Endpoints.Tasks;
using Enma.Api.ExceptionHandling;
using Enma.Api.Health;
using Enma.Api.Notifications;
using Enma.Application.Onboarding.RegisterOrganizationOwner;
using Enma.Application.Onboarding.RegisterInvitedUser;
using Enma.Application.Organizations.GetById;
using Enma.Infrastructure;
using Enma.Infrastructure.Documents.Storage;
using Enma.Infrastructure.Email;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.OpenApi;

var builder = WebApplication.CreateBuilder(args);

if (builder.Environment.IsProduction())
{
    string? dataProtectionKeysPath = builder.Configuration[
        "DataProtection:KeysPath"];
    if (string.IsNullOrWhiteSpace(dataProtectionKeysPath) ||
        !Path.IsPathFullyQualified(dataProtectionKeysPath) ||
        !Directory.Exists(dataProtectionKeysPath) ||
        !CanWriteToDirectory(dataProtectionKeysPath))
    {
        throw new InvalidOperationException(
            "Production data protection configuration is invalid.");
    }

    builder.Services
        .AddDataProtection()
        .SetApplicationName("Enma")
        .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysPath));
}

string connectionString = builder.Configuration.GetConnectionString("Database")
    ?? throw new InvalidOperationException(
        "The database connection string 'Database' is required.");

if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "The database connection string 'Database' is required.");
}

const string exactMoneyPattern = @"^-?(?:0|[1-9]\d*)(?:\.\d{1,2})?$";
builder.Services.AddOpenApi(options =>
{
    options.AddSchemaTransformer((schema, context, _) =>
    {
        if (context.JsonPropertyInfo?.PropertyType == typeof(decimal) &&
            context.JsonPropertyInfo.NumberHandling is
                JsonNumberHandling handling &&
            (handling & JsonNumberHandling.WriteAsString) != 0)
        {
            schema.Type = JsonSchemaType.String;
            schema.Format = null;
            schema.Pattern = exactMoneyPattern;
        }
        else if (context.JsonPropertyInfo?.PropertyType == typeof(decimal) &&
            context.JsonPropertyInfo.NumberHandling is
                JsonNumberHandling requestHandling &&
            (requestHandling & JsonNumberHandling.AllowReadingFromString) != 0)
        {
            schema.Format = null;
            schema.Pattern = exactMoneyPattern;
            schema.Description =
                "Use an invariant-culture decimal string for exact monetary " +
                "values; JSON numbers remain accepted for compatibility.";
        }

        return Task.CompletedTask;
    });
});
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = EnmaAntiforgeryDefaults.HeaderName;
    options.SuppressReadingTokenFromFormBody = true;
    AuthenticationCookies.ConfigureAntiforgery(options.Cookie);
});
AuthenticationBuilder authenticationBuilder = builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme =
            EnmaSessionAuthenticationDefaults.Scheme;
        options.DefaultChallengeScheme =
            EnmaSessionAuthenticationDefaults.Scheme;
    })
    .AddScheme<AuthenticationSchemeOptions, EnmaSessionAuthenticationHandler>(
        EnmaSessionAuthenticationDefaults.Scheme,
        _ => { })
    .AddCookie(ExternalAuthenticationDefaults.CookieScheme, options =>
    {
        options.Cookie.Name = "__Host-enma_google_external";
        options.Cookie.Path = "/";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.ExpireTimeSpan = TimeSpan.FromMinutes(10);
        options.SlidingExpiration = false;
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
    });

bool googleEnabled = builder.Configuration.GetValue<bool>(
    "Authentication:Google:Enabled");
string? googleClientId = builder.Configuration[
    "Authentication:Google:ClientId"];
string? googleClientSecret = builder.Configuration[
    "Authentication:Google:ClientSecret"];
string? googleFrontendOriginValue = builder.Configuration[
    "Authentication:Google:FrontendOrigin"];
Uri? googleFrontendOrigin = null;
if (!string.IsNullOrWhiteSpace(googleFrontendOriginValue) &&
    (!Uri.TryCreate(
        googleFrontendOriginValue,
        UriKind.Absolute,
        out googleFrontendOrigin) ||
     googleFrontendOrigin.Scheme is not ("http" or "https") ||
     googleFrontendOrigin.AbsolutePath != "/" ||
     !string.IsNullOrEmpty(googleFrontendOrigin.Query) ||
     !string.IsNullOrEmpty(googleFrontendOrigin.Fragment) ||
     !string.IsNullOrEmpty(googleFrontendOrigin.UserInfo) ||
     (googleFrontendOrigin.Scheme == "http" &&
        (!builder.Environment.IsDevelopment() ||
            !googleFrontendOrigin.IsLoopback))))
{
    throw new InvalidOperationException(
        "Google FrontendOrigin must be an absolute HTTPS origin without a path, query, fragment, or user information; loopback HTTP is allowed only in Development.");
}
if (googleEnabled &&
    (string.IsNullOrWhiteSpace(googleClientId) ||
        string.IsNullOrWhiteSpace(googleClientSecret)))
{
    throw new InvalidOperationException(
        "Enabled Google authentication requires ClientId and ClientSecret.");
}

builder.Services.AddSingleton(new GoogleAuthenticationAvailability(
    googleEnabled,
    googleFrontendOrigin));
if (googleEnabled)
{
    authenticationBuilder.AddOpenIdConnect(
        ExternalAuthenticationDefaults.GoogleScheme,
        options =>
        {
            options.Authority = "https://accounts.google.com";
            options.ClientId = googleClientId!;
            options.ClientSecret = googleClientSecret!;
            options.SignInScheme = ExternalAuthenticationDefaults.CookieScheme;
            options.CallbackPath = "/signin-google";
            options.ResponseType = "code";
            options.SaveTokens = false;
            options.UsePkce = true;
            options.MapInboundClaims = false;
            options.Scope.Clear();
            options.Scope.Add("openid");
            options.Scope.Add("email");
            options.Scope.Add("profile");
            options.TokenValidationParameters.NameClaimType = "name";
            options.TokenValidationParameters.ValidateIssuer = true;
            options.TokenValidationParameters.ValidateAudience = true;
            options.ProtocolValidator.RequireNonce = true;
            options.Events.OnRemoteFailure = context =>
            {
                context.HandleResponse();
                context.Response.Redirect(googleFrontendOrigin is null
                    ? "/login?google=failed"
                    : new Uri(
                        googleFrontendOrigin,
                        "/login?google=failed").AbsoluteUri);
                return Task.CompletedTask;
            };
        });
}
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(
        EnmaAuthorizationPolicies.OrganizationAccess,
        policy =>
        {
            policy.RequireAuthenticatedUser();
            policy.AddRequirements(new OrganizationAccessRequirement());
        });
});
builder.Services.AddScoped<
    IAuthorizationHandler,
    OrganizationAccessAuthorizationHandler>();
builder.Services.AddSingleton(serviceProvider =>
{
    IConfiguration configuration =
        serviceProvider.GetRequiredService<IConfiguration>();
    IHostEnvironment environment =
        serviceProvider.GetRequiredService<IHostEnvironment>();
    var options = new TrustedProxyOptions();
    configuration.GetSection(TrustedProxyOptions.SectionName).Bind(options);

    return TrustedProxyConfiguration.ValidateAndCreate(
        options,
        environment.IsProduction());
});
builder.Services
    .AddOptions<ForwardedHeadersOptions>()
    .Configure<TrustedProxyTrustSet>(
        (options, trustSet) => trustSet.Configure(options));
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = (context, _) =>
    {
        context.HttpContext.Response.Headers.CacheControl = "no-store";
        return ValueTask.CompletedTask;
    };

    options.AddPolicy(
        EmailVerificationEndpoints.ResendRateLimitPolicy,
        httpContext => RateLimitPartition.GetFixedWindowLimiter(
            GetClientIpPartitionKey(httpContext),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));

    options.AddPolicy(
        EmailVerificationEndpoints.VerifyRateLimitPolicy,
        httpContext => RateLimitPartition.GetFixedWindowLimiter(
            GetClientIpPartitionKey(httpContext),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 20,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));

    options.AddPolicy(
        PasswordRecoveryEndpoints.RequestRateLimitPolicy,
        httpContext => RateLimitPartition.GetFixedWindowLimiter(
            GetClientIpPartitionKey(httpContext),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));

    options.AddPolicy(
        PasswordRecoveryEndpoints.ResetRateLimitPolicy,
        httpContext => RateLimitPartition.GetFixedWindowLimiter(
            GetClientIpPartitionKey(httpContext),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 20,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));

    options.AddPolicy(
        OrganizationInvitationEndpoints.SendRateLimitPolicy,
        httpContext => RateLimitPartition.GetFixedWindowLimiter(
            GetClientIpPartitionKey(httpContext),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));

    options.AddPolicy(
        OrganizationInvitationEndpoints.RecipientTokenRateLimitPolicy,
        httpContext => RateLimitPartition.GetFixedWindowLimiter(
            GetClientIpPartitionKey(httpContext),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 20,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));

    options.AddPolicy(
        LoginEndpoints.RateLimitPolicy,
        httpContext => RateLimitPartition.GetFixedWindowLimiter(
            GetClientIpPartitionKey(httpContext),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));

    options.AddPolicy(
        GoogleAuthenticationEndpoints.RateLimitPolicy,
        httpContext => RateLimitPartition.GetFixedWindowLimiter(
            GetClientIpPartitionKey(httpContext),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 20,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
});
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddScoped<RegisterOrganizationOwnerHandler>();
builder.Services.AddScoped<RegisterInvitedUserHandler>();
builder.Services.AddScoped<GetOrganizationByIdHandler>();
builder.Services.AddInfrastructure(
    connectionString,
    builder.Configuration,
    builder.Environment.IsDevelopment());
if (builder.Environment.IsProduction())
{
    builder.Services.AddOptions<EmailVerificationDeliveryOptions>()
        .ValidateOnStart();
    builder.Services.AddOptions<EmailVerificationSendBudgetOptions>()
        .ValidateOnStart();
    builder.Services.AddOptions<DocumentStorageOptions>()
        .ValidateOnStart();
}
builder.Services.AddHealthChecks()
    .AddCheck<PostgreSqlReadinessHealthCheck>(
        "postgresql-schema",
        tags: ["ready"],
        timeout: TimeSpan.FromSeconds(5));
builder.Services.AddSingleton<
    INotificationGenerationCycleDelay,
    PeriodicNotificationGenerationCycleDelay>();
builder.Services.AddHostedService<NotificationGenerationWorker>();
builder.Services.AddSingleton<
    ILegalDocumentDeletionCycleDelay,
    PeriodicLegalDocumentDeletionCycleDelay>();
builder.Services.AddHostedService<LegalDocumentDeletionWorker>();

var app = builder.Build();

ProductionIngressConfiguration.Validate(
    app.Configuration,
    app.Environment);
TrustedProxyTrustSet trustedProxyTrustSet =
    app.Services.GetRequiredService<TrustedProxyTrustSet>();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseExceptionHandler();
if (trustedProxyTrustSet.Enabled)
{
    app.UseForwardedHeaders();
}

app.UseHttpsRedirection();
app.UseRateLimiter();
app.UseNoStoreResponses();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapLoginEndpoints();
app.MapGoogleAuthenticationEndpoints();
app.MapEmailVerificationEndpoints();
app.MapPasswordRecoveryEndpoints();
app.MapCsrfEndpoint();
app.MapLogoutEndpoint();
app.MapRegisterOrganizationOwnerEndpoint();
app.MapRegisterInvitedUserEndpoint();
app.MapCreateInitialOrganizationEndpoint();
app.MapOrganizationEndpoints();
app.MapCurrentUserOrganizationEndpoints();
app.MapOrganizationMemberEndpoints();
app.MapOrganizationInvitationEndpoints();
app.MapAuditLogEndpoints();
app.MapClientEndpoints();
app.MapFinanceEndpoints();
app.MapLegalProcessEndpoints();
app.MapLegalDeadlineEndpoints();
app.MapLegalTaskEndpoints();
app.MapLegalDocumentEndpoints();
app.MapCalendarEventEndpoints();
app.MapAgendaEndpoints();
app.MapDashboardEndpoints();
app.MapNotificationEndpoints();
RouteGroupBuilder health = app.MapGroup("/health")
    .RequireNoStoreResponses();
health.MapHealthChecks("/live", new HealthCheckOptions
{
    Predicate = _ => false
});
health.MapHealthChecks("/ready", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready")
});

app.Run();

static string GetClientIpPartitionKey(HttpContext httpContext)
{
    return httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}

static bool CanWriteToDirectory(string directoryPath)
{
    string probePath = Path.Combine(
        directoryPath,
        $".enma-write-probe-{Guid.NewGuid():N}");
    try
    {
        using FileStream probe = new(
            probePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 1,
            FileOptions.DeleteOnClose);
        probe.WriteByte(0);
        return true;
    }
    catch (IOException)
    {
        return false;
    }
    catch (UnauthorizedAccessException)
    {
        return false;
    }
}

public partial class Program;
