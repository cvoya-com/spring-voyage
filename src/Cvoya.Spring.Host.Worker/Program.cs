// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Worker;

using System.Runtime.InteropServices;

using Cvoya.Spring.Dapr.DependencyInjection;
using Cvoya.Spring.Dapr.Mcp;
using Cvoya.Spring.Host.Worker.Composition;
using Cvoya.Spring.Host.Worker.Endpoints;

public partial class Program
{
    public static async Task Main(string[] args)
    {
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

            // ADR-0052 / Wave 3 (#2625): the MCP surface is served as a route on
            // the worker's own Kestrel host. The default ASP.NET Core endpoint
            // (:8080) carries the Dapr actor handlers and /health; the MCP
            // JSON-RPC route binds to an *additional* Kestrel endpoint on the
            // MCP port (Mcp:Port, default 5050) so the agent-facing contract
            // — host.docker.internal:5050, the .mcp.json URL, deploy.sh's
            // `-p 5050:5050` — is unchanged, and the Dapr surface stays off the
            // agent-reachable port. ListenAnyIP binds the MCP port on all
            // interfaces, which keeps the worker's MCP socket reachable from
            // outside its own container: the agent reaches it through the
            // published `-p 5050:5050` mapping, not loopback (#1199).
            var mcpPort = builder.Configuration.GetValue<int?>("Mcp:Port")
                ?? new McpServerOptions().Port;
            if (mcpPort > 0)
            {
                builder.WebHost.ConfigureKestrel(options => options.ListenAnyIP(mcpPort));
            }

            var app = builder.Build();

            // Health check
            app.MapGet("/health", () => Results.Ok(new { Status = "Healthy" }));

            // Dapr actor endpoints
            app.MapActorsHandlers();

            // Internal execution-host routes (ADR-0052 / #2618, #2627). The
            // HTTP front door delegates persistent-agent deploy / undeploy /
            // scale / deployment-status / logs and unit-container teardown
            // here over Dapr service invocation; these routes wrap the
            // worker's PersistentAgentLifecycle / PersistentAgentRegistry /
            // IUnitContainerLifecycle. They sit on the Dapr :8080 app channel
            // and are not exposed on the public ingress.
            app.MapExecutionHostEndpoints();

            // MCP JSON-RPC route (ADR-0052 / #2625). Restricted to the MCP
            // Kestrel endpoint via the connection's local port — a request that
            // arrives on the Dapr :8080 endpoint is rejected so the MCP surface
            // and the Dapr surface stay cleanly separated. The handler reads the
            // request body + Authorization header from HttpContext and delegates
            // to the existing McpServer session store / JSON-RPC dispatch.
            if (mcpPort > 0)
            {
                app.MapPost("/mcp/", async (
                    HttpContext httpContext,
                    McpServer mcpServer,
                    CancellationToken cancellationToken) =>
                {
                    if (httpContext.Connection.LocalPort != mcpPort)
                    {
                        httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
                        return;
                    }

                    await mcpServer.HandleRequestAsync(httpContext, cancellationToken);
                });
            }

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
    }
}
