using System.Threading.RateLimiting;
using Enma.Api.Endpoints.Authentication;
using Enma.Api.Endpoints.Onboarding;
using Enma.Api.Endpoints.Organizations;
using Enma.Api.ExceptionHandling;
using Enma.Application.Onboarding.RegisterOrganizationOwner;
using Enma.Application.Organizations.GetById;
using Enma.Infrastructure;
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
});
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddScoped<RegisterOrganizationOwnerHandler>();
builder.Services.AddScoped<GetOrganizationByIdHandler>();
builder.Services.AddInfrastructure(connectionString, builder.Configuration);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseExceptionHandler();
app.UseHttpsRedirection();
app.UseRateLimiter();

app.MapEmailVerificationEndpoints();
app.MapRegisterOrganizationOwnerEndpoint();
app.MapOrganizationEndpoints();

app.Run();

static string GetClientIpPartitionKey(HttpContext httpContext)
{
    return httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}

public partial class Program;
