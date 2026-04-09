/*
 * Copyright CVOYA LLC.
 *
 * This source code is proprietary and confidential.
 * Unauthorized copying, modification, distribution, or use of this file,
 * via any medium, is strictly prohibited without the prior written consent of CVOYA LLC.
 */

using Cvoya.Spring.Dapr.DependencyInjection;
using Cvoya.Spring.Host.Api.Endpoints;

var builder = WebApplication.CreateBuilder(args);

var isLocalDev = args.Contains("--local") ||
    builder.Configuration.GetValue<bool>("LocalDev");

if (isLocalDev)
{
    builder.Configuration["LocalDev"] = "true";
}

builder.Services
    .AddCvoyaSpringCore()
    .AddCvoyaSpringDapr();

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

app.MapGet("/health", () => Results.Ok(new { Status = "Healthy" }))
    .WithTags("Health")
    .WithName("Health")
    .ExcludeFromDescription();

app.MapAgentEndpoints();
app.MapUnitEndpoints();
app.MapMessageEndpoints();
app.MapDirectoryEndpoints();

await app.RunAsync();

/// <summary>
/// Partial class to enable WebApplicationFactory-based integration testing.
/// </summary>
public partial class Program;
