/*
 * Copyright CVOYA LLC.
 *
 * This source code is proprietary and confidential.
 * Unauthorized copying, modification, distribution, or use of this file,
 * via any medium, is strictly prohibited without the prior written consent of CVOYA LLC.
 */

using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.DependencyInjection;
using Cvoya.Spring.Dapr.Workflows;
using Cvoya.Spring.Dapr.Workflows.Activities;
using Dapr.Actors;
using Dapr.Actors.Runtime;
using Dapr.Workflow;

var builder = WebApplication.CreateBuilder(args);

// Force-exit after 5 seconds when Ctrl+C is pressed. The Durable Task gRPC
// worker ignores cancellation and retries indefinitely when the sidecar is
// down, which makes the process unkillable via normal shutdown.
builder.Services.Configure<HostOptions>(options =>
    options.ShutdownTimeout = TimeSpan.FromSeconds(5));

var lifetime = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    if (lifetime.IsCancellationRequested)
    {
        // Second Ctrl+C — force exit immediately
        Environment.Exit(1);
    }
    lifetime.Cancel();
    _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ => Environment.Exit(1));
};

// Register Spring services
builder.Services
    .AddCvoyaSpringCore()
    .AddCvoyaSpringDapr(builder.Configuration);

// Register Dapr workflows
builder.Services.AddDaprWorkflow(options =>
{
    options.RegisterWorkflow<AgentLifecycleWorkflow>();
    options.RegisterWorkflow<CloningLifecycleWorkflow>();
    options.RegisterActivity<ValidateAgentDefinitionActivity>();
    options.RegisterActivity<RegisterAgentActivity>();
    options.RegisterActivity<UnregisterAgentActivity>();
});

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

await app.RunAsync(lifetime.Token);
