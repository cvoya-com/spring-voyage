// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

/// <summary>
/// Configuration options for <see cref="DispatcherClientContainerRuntime"/>.
/// Bound from the <c>Dispatcher</c> configuration section in the worker host.
/// </summary>
public class DispatcherClientOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Dispatcher";

    /// <summary>
    /// Base URL of the <c>spring-dispatcher</c> service (e.g.
    /// <c>http://spring-dispatcher:8080/</c>). When unset, the client throws
    /// on the first call — which surfaces the misconfiguration at dispatch
    /// time rather than silently falling back to an in-process runtime.
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// Bearer token the worker presents to the dispatcher on every request.
    /// Issued at deploy time and scoped to a tenant on the dispatcher side.
    /// </summary>
    public string? BearerToken { get; set; }
}