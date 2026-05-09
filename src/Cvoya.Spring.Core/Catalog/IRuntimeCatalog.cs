// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Catalog;

/// <summary>
/// Read-only access to the platform's runtime catalogue
/// (<c>platform/runtime-catalog.yaml</c>) — the canonical source for
/// agent-runtime and model-provider definitions per ADR-0038. Loaded
/// once at startup.
/// </summary>
/// <remarks>
/// <para>
/// Lookups on <see cref="GetAgentRuntime"/> and
/// <see cref="GetModelProvider"/> are case-insensitive against id. The
/// default implementation lives in <c>Cvoya.Spring.RuntimeCatalog</c>;
/// alternative implementations (test fakes, tenant-scoped overrides) slot
/// in via DI.
/// </para>
/// </remarks>
public interface IRuntimeCatalog
{
    /// <summary>Every agent runtime declared in the catalogue.</summary>
    IReadOnlyList<AgentRuntime> AgentRuntimes { get; }

    /// <summary>Every model provider declared in the catalogue.</summary>
    IReadOnlyList<ModelProvider> ModelProviders { get; }

    /// <summary>
    /// Looks up an agent runtime by its stable <see cref="AgentRuntime.Id"/>.
    /// Case-insensitive. Returns <c>null</c> when no entry matches.
    /// </summary>
    AgentRuntime? GetAgentRuntime(string id);

    /// <summary>
    /// Looks up a model provider by its stable <see cref="ModelProvider.Id"/>.
    /// Case-insensitive. Returns <c>null</c> when no entry matches.
    /// </summary>
    ModelProvider? GetModelProvider(string id);
}
