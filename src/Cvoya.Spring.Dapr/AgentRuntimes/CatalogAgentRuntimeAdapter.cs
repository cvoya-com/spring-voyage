// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.AgentRuntimes;

using Cvoya.Spring.Core.AgentRuntimes;
using Cvoya.Spring.Core.Catalog;

/// <summary>
/// Internal adapter that synthesises a legacy <see cref="IAgentRuntime"/>
/// projection from a <see cref="Cvoya.Spring.Core.Catalog.AgentRuntime"/>
/// catalogue entry. Per ADR-0038 the per-provider <c>IAgentRuntime</c>
/// classes are gone; the catalogue is the source of truth. This adapter
/// keeps the legacy host-side surface (Web API DTOs that emit
/// <c>kind</c> / <c>credentialKind</c> / <c>credentialSecretName</c> /
/// <c>defaultImage</c>) byte-identical until PR-1b reshapes the wire
/// contract.
/// </summary>
/// <remarks>
/// <para>
/// The adapter derives legacy fields from the catalogue:
/// <list type="bullet">
/// <item><see cref="IAgentRuntime.Kind"/> = <c>AgentRuntime.Launcher</c> (the launcher strategy id, e.g. <c>claude-code-cli</c>).</item>
/// <item><see cref="IAgentRuntime.CredentialSchema"/> = derived from the runtime's first <see cref="AgentRuntimeProviderEdge"/> (its single accepted auth method).</item>
/// <item><see cref="IAgentRuntime.CredentialSecretName"/> = canonical <c>{provider}-{authMethod-slug}</c> per ADR-0038 § "Credential identity".</item>
/// <item><see cref="IAgentRuntime.CredentialEnvVar"/> = the first edge's <c>CredentialEnvVar</c>.</item>
/// <item><see cref="IAgentRuntime.DefaultImage"/> = catalogue value.</item>
/// <item><see cref="IAgentRuntime.DefaultModels"/> = the catalogue's first provider's <c>DefaultModels</c>, mapped to <see cref="ModelDescriptor"/>.</item>
/// </list>
/// </para>
/// <para>
/// Live-fetch / credential-validation / format-acceptance rely on the
/// per-runtime provider's <c>IModelProviderAdapter</c>; for now those
/// surfaces return Unsupported / Unknown — PR-1b reshapes the host
/// endpoints to read these signals from the provider adapter directly,
/// at which point this synthesised projection drops below the
/// dispatcher and the legacy interface goes away entirely.
/// </para>
/// </remarks>
internal sealed class CatalogAgentRuntimeAdapter : IAgentRuntime
{
    private readonly Core.Catalog.AgentRuntime _runtime;
    private readonly ModelProvider? _primaryProvider;
    private readonly AgentRuntimeProviderEdge? _primaryEdge;

    public CatalogAgentRuntimeAdapter(
        Core.Catalog.AgentRuntime runtime,
        IRuntimeCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(catalog);

        _runtime = runtime;
        _primaryEdge = runtime.ModelProviders.Count > 0 ? runtime.ModelProviders[0] : null;
        _primaryProvider = _primaryEdge is not null ? catalog.GetModelProvider(_primaryEdge.Id) : null;
    }

    /// <inheritdoc />
    public string Id => _runtime.Id;

    /// <inheritdoc />
    public string DisplayName => _runtime.DisplayName;

    /// <inheritdoc />
    public string Kind => _runtime.Launcher;

    /// <inheritdoc />
    public AgentRuntimeCredentialSchema CredentialSchema =>
        _primaryEdge?.AuthMethod switch
        {
            AuthMethod.Oauth => new AgentRuntimeCredentialSchema(AgentRuntimeCredentialKind.OAuthToken),
            AuthMethod.ApiKey => new AgentRuntimeCredentialSchema(AgentRuntimeCredentialKind.ApiKey),
            _ => new AgentRuntimeCredentialSchema(AgentRuntimeCredentialKind.None),
        };

    /// <inheritdoc />
    /// <remarks>
    /// PR-1a transitional: emits the legacy <c>{provider}-api-key</c>
    /// shape so wire DTOs stay byte-identical against <c>origin/main</c>.
    /// PR-1b switches the wire to expose the per-edge canonical
    /// <see cref="CredentialNaming.SecretNameFor"/> form
    /// (<c>{provider}-{authMethod-slug}</c>).
    /// </remarks>
    public string CredentialSecretName
    {
        get
        {
            if (_primaryEdge is null || _primaryEdge.AuthMethod is null)
            {
                return string.Empty;
            }

            return $"{_primaryEdge.Id.ToLowerInvariant()}-api-key";
        }
    }

    /// <inheritdoc />
    public string CredentialEnvVar => _primaryEdge?.CredentialEnvVar ?? string.Empty;

    /// <inheritdoc />
    public IReadOnlyList<ModelDescriptor> DefaultModels =>
        _primaryProvider?.DefaultModels
            .Select(id => new ModelDescriptor(id, id, null))
            .ToArray()
        ?? Array.Empty<ModelDescriptor>();

    /// <inheritdoc />
    public string DefaultImage => _runtime.DefaultImage;

    /// <inheritdoc />
    public IReadOnlyList<ProbeStep> GetProbeSteps(AgentRuntimeInstallConfig config, string credential)
    {
        // The probe-build path is owned by IAgentRuntimeLauncher per
        // ADR-0038. Callers should resolve the launcher from
        // IAgentRuntimeLauncherRegistry and call its GetProbeSteps; this
        // shim provides a placeholder for the few legacy surfaces still
        // routed through the registry. Returning empty is the documented
        // "nothing to probe" contract.
        return Array.Empty<ProbeStep>();
    }

    /// <inheritdoc />
    public Task<FetchLiveModelsResult> FetchLiveModelsAsync(
        string credential,
        CancellationToken cancellationToken = default)
    {
        // ADR-0038: live-fetch belongs on IModelProviderAdapter (PR-1b
        // exposes this through the reshaped /model-providers/{id}/refresh-models
        // endpoint). The legacy runtime-keyed path returns Unsupported so
        // callers fall back to the seed catalogue.
        return Task.FromResult(new FetchLiveModelsResult(
            FetchLiveModelsStatus.Unsupported,
            Array.Empty<ModelDescriptor>(),
            "Live-fetch is provider-keyed under ADR-0038; route through the model-provider adapter."));
    }

    /// <inheritdoc />
    public bool IsCredentialFormatAccepted(string credential, CredentialDispatchPath dispatchPath)
    {
        if (string.IsNullOrWhiteSpace(credential))
        {
            return true;
        }

        if (_primaryEdge?.AuthMethod is null)
        {
            return true;
        }

        // Strict per-path acceptance carried over from the legacy
        // IAgentRuntime.IsCredentialFormatAccepted contract:
        //
        // - The Anthropic Platform REST endpoint rejects OAuth tokens
        //   (sk-ant-oat…). Every other shape passes the format check.
        // - The Claude Code agent-runtime path accepts OAuth tokens; it
        //   rejects Anthropic Platform API keys (sk-ant-api…) for the
        //   same reason — the CLI's `setup-token` flow stores OAuth
        //   tokens, not API keys.
        // - All other (provider, dispatch path) combinations accept any
        //   shape (the resolver / live probe surfaces real auth failures
        //   later).
        var providerId = _primaryEdge.Id;
        if (string.Equals(providerId, "anthropic", StringComparison.OrdinalIgnoreCase))
        {
            if (dispatchPath == CredentialDispatchPath.Rest
                && credential.StartsWith("sk-ant-oat", StringComparison.Ordinal))
            {
                return false;
            }
            if (dispatchPath == CredentialDispatchPath.AgentRuntime
                && credential.StartsWith("sk-ant-api", StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}