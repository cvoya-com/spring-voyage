// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.DependencyInjection;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

/// <summary>
/// Workaround helpers for the Dapr SDK <c>WorkflowWorker</c> shutdown bug
/// tracked as <see href="https://github.com/cvoya-com/spring-voyage/issues/568">spring-voyage#568</see>
/// (upstream <c>dapr/dotnet-sdk</c>, confirmed against
/// <c>Dapr.Workflow 1.17.8</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>What is broken upstream.</b>
/// <c>Dapr.Workflow.Worker.Grpc.GrpcProtocolHandler.DisposeAsync()</c>
/// throws <see cref="ObjectDisposedException"/> from
/// <c>WorkflowWorker.StopAsync</c> during host shutdown when its internal
/// <c>_disposalCts</c> was disposed without first being canceled. Two
/// independent defects in its idempotency guard:
/// </para>
/// <list type="number">
///   <item>
///     <b>TOCTOU race</b> — two concurrent <c>DisposeAsync</c> callers can
///     both observe <c>_disposalCts.IsCancellationRequested == false</c>
///     and both step past the guard; the first runs Cancel + Dispose, the
///     second then calls <c>CancelAsync</c> on a disposed CTS.
///   </item>
///   <item>
///     <b>Wrong predicate</b> — <c>IsCancellationRequested</c> is only set
///     by <c>Cancel</c>/<c>CancelAsync</c>, never by <c>Dispose</c>. A CTS
///     that ends up disposed without first being canceled still reports
///     <c>IsCancellationRequested == false</c>, so the guard waves the
///     next <c>DisposeAsync</c> straight into the throwing path.
///   </item>
/// </list>
/// <para>
/// <b>When this matters for us.</b> Production hosts run with a real
/// sidecar and a fully exercised workflow worker, where the race almost
/// never trips. The bug surfaces deterministically in two scenarios that
/// load the worker without a sidecar:
/// </para>
/// <list type="bullet">
///   <item>
///     Build-time OpenAPI generation (<c>GetDocument.Insider</c>): the
///     worker starts a gRPC bidirectional stream that immediately fails
///     "Connection refused" and floods the build with errors. See
///     <see href="https://github.com/cvoya-com/spring-voyage/issues/370">#370</see>.
///   </item>
///   <item>
///     <see cref="WebApplicationFactory"/>-based integration tests: host
///     teardown drives <c>StopAsync</c> while the failing gRPC retry loop
///     is also unwinding through <c>DisposeAsync</c>, surfacing as
///     "Test Class Cleanup Failure" entries from xUnit. See
///     <see href="https://github.com/cvoya-com/spring-voyage/issues/568">#568</see>.
///   </item>
/// </list>
/// <para>
/// <b>What this helper does.</b> Both scenarios are fixed identically by
/// removing the Dapr <see cref="IHostedService"/> registration that
/// <c>AddDaprWorkflow</c> adds (the <c>WorkflowWorker</c> background
/// service). The <c>DaprWorkflowClient</c> and the rest of the workflow
/// DI graph stay registered, so endpoint code that references workflow
/// types still resolves at runtime — only the background gRPC stream is
/// suppressed.
/// </para>
/// <para>
/// <b>Lifetime.</b> Delete this helper and its callers once we upgrade
/// past a Dapr SDK release that fixes the upstream guard. Tracking issue:
/// <see href="https://github.com/cvoya-com/spring-voyage/issues/568">#568</see>.
/// </para>
/// </remarks>
public static class DaprWorkflowWorkerWorkaround
{
    /// <summary>
    /// Removes the Dapr Workflow <see cref="IHostedService"/> registration
    /// added by <c>AddDaprWorkflow</c>, leaving the rest of the workflow
    /// DI graph (notably <c>DaprWorkflowClient</c>) intact.
    /// </summary>
    /// <remarks>
    /// Call this immediately after <c>AddDaprWorkflow</c> in any host that
    /// must not start the worker — design-time tooling, integration test
    /// harnesses, etc. Idempotent and safe to call when the worker
    /// registration is not present (no-op).
    /// </remarks>
    /// <param name="services">The service collection to mutate.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection RemoveDaprWorkflowWorker(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Match by namespace prefix so this keeps working if the SDK
        // renames the concrete type (e.g. WorkflowWorker -> something
        // else). The strip is intentionally narrow: only IHostedService
        // descriptors whose ImplementationType lives under Dapr.Workflow.
        var workerDescriptors = services
            .Where(d => d.ServiceType == typeof(IHostedService)
                && d.ImplementationType?.FullName?.StartsWith(
                    "Dapr.Workflow.", StringComparison.Ordinal) == true)
            .ToList();

        foreach (var descriptor in workerDescriptors)
        {
            services.Remove(descriptor);
        }

        return services;
    }
}