// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

using System.Runtime.InteropServices;

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

await app.RunAsync();

/// <summary>
/// Partial class to enable WebApplicationFactory-based integration testing.
/// </summary>
public partial class Program;