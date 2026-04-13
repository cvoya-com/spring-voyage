// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

using System.Text.Json.Serialization;

using Cvoya.Spring.Connector.GitHub.DependencyInjection;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Dapr.DependencyInjection;
using Cvoya.Spring.Host.Api.Auth;
using Cvoya.Spring.Host.Api.Endpoints;
using Cvoya.Spring.Host.Api.Services;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;

var builder = WebApplication.CreateBuilder(args);

var isLocalDev = args.Contains("--local") ||
    builder.Configuration.GetValue<bool>("LocalDev");

if (isLocalDev)
{
    builder.Configuration["LocalDev"] = "true";
}

builder.Services
    .AddCvoyaSpringCore()
    .AddCvoyaSpringDapr(builder.Configuration)
    .AddCvoyaSpringConnectorGitHub(builder.Configuration)
    .AddCvoyaSpringApiServices(builder.Configuration);

if (isLocalDev)
{
    builder.Services.AddAuthentication(AuthConstants.LocalDevScheme)
        .AddScheme<AuthenticationSchemeOptions, LocalDevAuthHandler>(AuthConstants.LocalDevScheme, null);
}
else
{
    builder.Services.AddAuthentication(AuthConstants.ApiTokenScheme)
        .AddScheme<AuthenticationSchemeOptions, ApiTokenAuthHandler>(AuthConstants.ApiTokenScheme, null);
}

builder.Services.AddAuthorization(options => options.AddUnitPermissionPolicies());
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<IAuthorizationHandler, PermissionHandler>();

// Serialize every enum as its string name (case-insensitive on the way in)
// so clients get "Running" instead of 3 and don't have to reconstruct the
// numeric ordering. Individual endpoints no longer need to call .ToString().
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(
        new JsonStringEnumConverter(allowIntegerValues: false));
});

builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

if (app.Environment.IsDevelopment() || isLocalDev)
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { Status = "Healthy" }))
    .WithTags("Health")
    .WithName("Health")
    .ExcludeFromDescription();

app.MapAuthEndpoints();
app.MapAgentEndpoints().RequireAuthorization();
app.MapUnitEndpoints().RequireAuthorization();
app.MapPackageEndpoints().RequireAuthorization();
app.MapMessageEndpoints().RequireAuthorization();
app.MapDirectoryEndpoints().RequireAuthorization();
app.MapCloneEndpoints().RequireAuthorization();
app.MapCostEndpoints().RequireAuthorization();
app.MapBudgetEndpoints().RequireAuthorization();
app.MapInitiativeEndpoints().RequireAuthorization();
app.MapActivityEndpoints().RequireAuthorization();
app.MapDashboardEndpoints().RequireAuthorization();
app.MapSkillsEndpoints().RequireAuthorization();
app.MapWebhookEndpoints();

await app.RunAsync();

/// <summary>
/// Partial class to enable WebApplicationFactory-based integration testing.
/// </summary>
public partial class Program;