// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

using System.Runtime.InteropServices;

using Cvoya.Spring.Dapr.DependencyInjection;
using Cvoya.Spring.Host.Worker.Composition;

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

// Fail-fast guard: if composition or host start throws, log the exception
// and Environment.Exit(1) so podman/systemd can restart the container.
// Without this, the process can remain alive (PID 1) while the host
// lifetime is broken — podman reports the container as "Up" with
// ExitCode 0, and `unless-stopped` never fires. See #587.
try
{
    // Register Spring services, Dapr workflows, and Dapr actors via the shared
    // composition helper. The Worker composition smoke test rides the same helper
    // so any registration gap surfaces at `dotnet test` time rather than at
    // container startup. See #586 and WorkerComposition.cs.
    builder.Services.AddWorkerServices(builder.Configuration);

    var app = builder.Build();

    // Health check
    app.MapGet("/health", () => Results.Ok(new { Status = "Healthy" }));

    // Dapr actor endpoints
    app.MapActorsHandlers();

    // Drive EF Core migrations to completion BEFORE any hosted service
    // starts. The Generic Host invokes IHostedService.StartAsync in
    // registration order, but several services in AddCvoyaSpringDapr's
    // graph (registered before AddCvoyaSpringDatabaseMigrator in
    // WorkerComposition) query spring.unit_definitions on a fresh
    // PostgreSQL volume — the migrator hasn't created the table yet,
    // and the cold start logs a 42P01 relation-not-exist line per
    // affected service. Running the migrator here, before RunAsync,
    // guarantees the schema exists before any of those services
    // execute. The migrator's hosted-service registration stays in
    // place and StartAsync is idempotent (DatabaseMigrator.HasRun), so
    // the host's later invocation short-circuits. See #1608.
    await app.MigrateSpringDatabaseAsync();

    await app.RunAsync();
}
catch (Exception ex)
{
    Console.Error.WriteLine("FATAL: Host.Worker failed to start. Exiting with code 1 so the container orchestrator can restart the process.");
    Console.Error.WriteLine(ex.ToString());
    Environment.Exit(1);
}

/// <summary>
/// Partial class to enable WebApplicationFactory-based integration testing.
/// </summary>
public partial class Program;