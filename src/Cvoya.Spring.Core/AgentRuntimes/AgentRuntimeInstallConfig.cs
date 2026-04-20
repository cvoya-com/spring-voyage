// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.AgentRuntimes;

/// <summary>
/// Tenant-scoped configuration for an installed
/// <see cref="IAgentRuntime"/>. Persisted as JSON in
/// <c>tenant_agent_runtime_installs.config</c> and surfaced via the
/// <c>/api/v1/agent-runtimes/{id}</c> endpoints.
/// </summary>
/// <param name="Models">
/// Model ids the tenant has enabled for this runtime. When empty, callers
/// that need a list should fall back to the runtime's
/// <see cref="IAgentRuntime.DefaultModels"/>.
/// </param>
/// <param name="DefaultModel">
/// Preferred model id — used by the wizard to pre-select a value. When
/// <c>null</c> the first entry of <paramref name="Models"/> is used.
/// </param>
/// <param name="BaseUrl">
/// Optional base URL override used by runtimes that support self-hosted
/// or proxied endpoints (e.g. Ollama, OpenAI-compatible gateways).
/// </param>
public sealed record AgentRuntimeInstallConfig(
    IReadOnlyList<string> Models,
    string? DefaultModel,
    string? BaseUrl)
{
    /// <summary>Empty config with no models, default, or base URL.</summary>
    public static readonly AgentRuntimeInstallConfig Empty =
        new(Array.Empty<string>(), null, null);

    /// <summary>
    /// Produces an install-config built from the runtime's seed catalog.
    /// First entry (if any) becomes the default model. Used when a tenant
    /// installs a runtime without supplying an explicit configuration.
    /// </summary>
    /// <param name="runtime">The runtime whose default model list seeds the config.</param>
    public static AgentRuntimeInstallConfig FromRuntimeDefaults(IAgentRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        var models = runtime.DefaultModels.Select(m => m.Id).ToArray();
        return new AgentRuntimeInstallConfig(
            Models: models,
            DefaultModel: models.Length > 0 ? models[0] : null,
            BaseUrl: null);
    }
}