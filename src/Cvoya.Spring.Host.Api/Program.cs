// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

using Cvoya.Spring.Dapr.DependencyInjection;
using Cvoya.Spring.Host.Api.Auth;
using Cvoya.Spring.Host.Api.Endpoints;

using Microsoft.AspNetCore.Authentication;

var builder = WebApplication.CreateBuilder(args);

var isLocalDev = args.Contains("--local") ||
    builder.Configuration.GetValue<bool>("LocalDev");

if (isLocalDev)
{
    builder.Configuration["LocalDev"] = "true";
}

builder.Services
    .AddCvoyaSpringCore()
    .AddCvoyaSpringDapr(builder.Configuration);

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

builder.Services.AddAuthorization();

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
app.MapMessageEndpoints().RequireAuthorization();
app.MapDirectoryEndpoints().RequireAuthorization();

await app.RunAsync();

/// <summary>
/// Partial class to enable WebApplicationFactory-based integration testing.
/// </summary>
public partial class Program;