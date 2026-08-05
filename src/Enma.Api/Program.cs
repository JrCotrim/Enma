using Enma.Api.Endpoints.Organizations;
using Enma.Api.ExceptionHandling;
using Enma.Application.Organizations.Create;
using Enma.Application.Organizations.GetById;
using Enma.Infrastructure;

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
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddScoped<CreateOrganizationHandler>();
builder.Services.AddScoped<GetOrganizationByIdHandler>();
builder.Services.AddInfrastructure(connectionString);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseExceptionHandler();
app.UseHttpsRedirection();

app.MapOrganizationEndpoints();

app.Run();

public partial class Program;
