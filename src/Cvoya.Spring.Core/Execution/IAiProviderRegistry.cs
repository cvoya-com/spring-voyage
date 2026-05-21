// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Execution;

/// <summary>
/// Resolves <see cref="IAiProvider"/> instances by their stable <see cref="IAiProvider.Id"/>.
/// Each provider's id matches the manifest's <c>execution.provider</c> slot value
/// (<c>anthropic</c>, <c>ollama</c>, …); routing strategies inside <c>UnitActor</c>
/// consult the registry at dispatch time so a unit configured with
/// <c>provider: ollama</c> hits <c>OllamaProvider</c> rather than whatever happens
/// to be registered as the default <see cref="IAiProvider"/> in DI.
/// </summary>
/// <remarks>
/// Pairs with <see cref="IAgentRuntimeLauncherRegistry"/>: this registry
/// indexes single-shot REST providers, that one indexes container
/// launchers. Both enumerate their respective DI services once at
/// construction and answer subsequent <c>Get</c> calls in O(1); the
/// dictionary is built up-front so a misconfigured deployment with two
/// providers reporting the same id fails at host start, not on first
/// dispatch.
/// </remarks>
public interface IAiProviderRegistry
{
    /// <summary>
    /// Resolve a provider by its id. Returns <c>null</c> when no provider
    /// matches, leaving error handling to the caller (the execution
    /// layer surfaces a <c>ProbeInternalError</c>-style result so the
    /// operator sees an actionable message instead of a 502).
    /// </summary>
    /// <param name="providerId">The provider id (case-insensitive).</param>
    IAiProvider? Get(string providerId);

    /// <summary>
    /// Every registered provider, in DI registration order. Used by
    /// platform-level surfaces (status endpoint, model-catalog listing)
    /// that need to enumerate the available providers.
    /// </summary>
    IReadOnlyList<IAiProvider> All { get; }
}
