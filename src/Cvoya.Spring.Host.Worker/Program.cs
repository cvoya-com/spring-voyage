// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

using System.Runtime.InteropServices;

using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.DependencyInjection;
using Cvoya.Spring.Dapr.Workflows;
using Cvoya.Spring.Dapr.Workflows.Activities;

using Dapr.Actors;
using Dapr.Actors.Runtime;
using Dapr.Workflow;

var builder = WebApplication.CreateBuilder(args);

// Force-exit on shutdown signals. The Durable Task gRPC worker ignores
// cancellation and retries indefinitely when the sidecar is down.
// We handle both SIGINT (Ctrl+C) and SIGTERM (sent by `dapr run` to the
// child process) and use a raw thread for the timeout because the thread
// pool may be saturated by gRPC retries.
builder.Services.Configure<HostOptions>(options =>
    options.ShutdownTimeout = TimeSpan.FromSeconds(5));

var shutdownRequested = 0;

void ForceExitOnSignal()
{
    if (Interlocked.Increment(ref shutdownRequested) > 1)
    {
        // Second signal (e.g. SIGTERM from dapr after SIGINT) — exit immediately
        Environment.Exit(0);
    }
    // First signal — give 5 seconds then force exit
    new Thread(() =>
    {
        Thread.Sleep(5000);
        Environment.Exit(1);
    })
    { IsBackground = true }.Start();
}

using var sigInt = PosixSignalRegistration.Create(PosixSignal.SIGINT, _ => ForceExitOnSignal());
using var sigTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => ForceExitOnSignal());

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

await app.RunAsync();