/*
 * Copyright CVOYA LLC.
 *
 * This source code is proprietary and confidential.
 * Unauthorized copying, modification, distribution, or use of this file,
 * via any medium, is strictly prohibited without the prior written consent of CVOYA LLC.
 */

using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.DependencyInjection;
using Dapr.Actors;
using Dapr.Actors.Runtime;

var builder = WebApplication.CreateBuilder(args);

// Register Spring services
builder.Services
    .AddCvoyaSpringCore()
    .AddCvoyaSpringDapr();

// Register Dapr actors
builder.Services.AddActors(options =>
{
    options.Actors.RegisterActor<AgentActor>();
    options.Actors.RegisterActor<UnitActor>();
    options.Actors.RegisterActor<ConnectorActor>();
    options.Actors.RegisterActor<HumanActor>();

    options.ActorIdleTimeout = TimeSpan.FromHours(1);
    options.ActorScanInterval = TimeSpan.FromSeconds(30);
    options.ReentrancyConfig = new ActorReentrancyConfig { Enabled = false };
});

var app = builder.Build();

// Health check
app.MapGet("/health", () => Results.Ok(new { Status = "Healthy" }));

// Dapr actor endpoints
app.MapActorsHandlers();

await app.RunAsync();
