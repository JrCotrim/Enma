using System.Threading.RateLimiting;
using Enma.Api.Authentication;
using Enma.Api.Authorization;
using Enma.Api.Deployment;
using Enma.Api.Endpoints;
using Enma.Api.Endpoints.Authentication;
using Enma.Api.Endpoints.Clients;
using Enma.Api.Endpoints.Deadlines;
using Enma.Api.Endpoints.Documents;
using Enma.Api.Endpoints.Onboarding;
using Enma.Api.Endpoints.Organizations;
using Enma.Api.Endpoints.Processes;
using Enma.Api.Endpoints.Tasks;
using Enma.Api.ExceptionHandling;
using Enma.Application.Onboarding.RegisterOrganizationOwner;
using Enma.Application.Organizations.GetById;
using Enma.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

string connectionString = builder.Configuration.GetConnectionString("Database")
    ?? throw new InvalidOperationException(
        "The database connection string 'Database' is required.");

if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "The database connection string 'Database' is required.");
}

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = EnmaAntiforgeryDefaults.HeaderName;
    options.SuppressReadingTokenFromFormBody = true;
    AuthenticationCookies.ConfigureAntiforgery(options.Cookie);
});
builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme =
            EnmaSessionAuthenticationDefaults.Scheme;
        options.DefaultChallengeScheme =
            EnmaSessionAuthenticationDefaults.Scheme;
    })
    .AddScheme<AuthenticationSchemeOptions, EnmaSessionAuthenticationHandler>(
        EnmaSessionAuthenticationDefaults.Scheme,
        _ => { });
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
});
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddScoped<RegisterOrganizationOwnerHandler>();
builder.Services.AddScoped<GetOrganizationByIdHandler>();
builder.Services.AddInfrastructure(
    connectionString,
    builder.Configuration,
    builder.Environment.IsDevelopment());

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
app.MapEmailVerificationEndpoints();
app.MapCsrfEndpoint();
app.MapLogoutEndpoint();
app.MapRegisterOrganizationOwnerEndpoint();
app.MapOrganizationEndpoints();
app.MapCurrentUserOrganizationEndpoints();
app.MapOrganizationMemberEndpoints();
app.MapClientEndpoints();
app.MapLegalProcessEndpoints();
app.MapLegalDeadlineEndpoints();
app.MapLegalTaskEndpoints();
app.MapLegalDocumentEndpoints();

app.Run();

static string GetClientIpPartitionKey(HttpContext httpContext)
{
    return httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}

public partial class Program;
