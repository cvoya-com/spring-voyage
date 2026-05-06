// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

using Cvoya.Spring.Core.AgentRuntimes;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Secrets;
using Cvoya.Spring.Core.Tenancy;

using Microsoft.Extensions.Logging;

/// <summary>
/// Default <see cref="ILlmCredentialResolver"/> implementation (#615).
/// Delegates to the existing <see cref="ISecretResolver"/> (which already
/// implements the Unit → Tenant inheritance fall-through, ADR 0003).
/// Credentials must be set as unit- or tenant-scoped secrets via
/// <c>spring secret --scope tenant</c> or the Tenant defaults panel.
/// </summary>
/// <remarks>
/// <para>
/// The canonical secret name is read from the runtime's
/// <see cref="IAgentRuntime.CredentialSecretName"/> via
/// <see cref="IAgentRuntimeRegistry"/>. That keeps the mapping next to the
/// runtime plugin (where it is declared) instead of duplicated on a
/// host-side switch. Unknown provider ids — and runtimes whose
/// <see cref="AgentRuntimeCredentialSchema"/> declares no credential — return
/// <see cref="LlmCredentialSource.NotFound"/> without consulting the secret
/// store.
/// </para>
/// <para>
/// <b>Why ID-based lookup.</b> Using a deterministic name keeps the
/// resolver stateless and the CLI ergonomic: operators do not have to
/// remember provider-specific names — the platform always asks the
/// resolver, the resolver always knows which name to look up.
/// </para>
/// </remarks>
public sealed class LlmCredentialResolver : ILlmCredentialResolver
{
    private readonly IAgentRuntimeRegistry _registry;
    private readonly ISecretResolver _secretResolver;
    private readonly ITenantContext _tenantContext;
    private readonly ILogger<LlmCredentialResolver> _logger;

    /// <summary>
    /// Creates a new <see cref="LlmCredentialResolver"/>.
    /// </summary>
    public LlmCredentialResolver(
        IAgentRuntimeRegistry registry,
        ISecretResolver secretResolver,
        ITenantContext tenantContext,
        ILogger<LlmCredentialResolver> logger)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(secretResolver);
        ArgumentNullException.ThrowIfNull(tenantContext);
        ArgumentNullException.ThrowIfNull(logger);

        _registry = registry;
        _secretResolver = secretResolver;
        _tenantContext = tenantContext;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<LlmCredentialResolution> ResolveAsync(
        string providerId,
        Guid? agentId,
        Guid? unitId,
        CancellationToken cancellationToken = default)
    {
        // #1714 step 0: agentId is accepted today so launchers and the
        // system-status endpoint can pass it through without API churn
        // when the agent-scope storage tier (#1737) lands. The OSS
        // default routes resolution through the existing Unit→Tenant
        // chain; the private cloud overlay or a future Agent scope
        // override on top of this resolver can layer agent-scope
        // resolution before falling through here.
        _ = agentId;

        var secretName = ResolveSecretName(providerId);
        if (string.IsNullOrEmpty(secretName))
        {
            return new LlmCredentialResolution(null, LlmCredentialSource.NotFound, string.Empty);
        }

        // A SecretUnreadableException here means a slot exists but its
        // ciphertext did not authenticate — typically because the at-rest
        // encryption key rotated between the write and the read. That's
        // an operational state, not a crash: surface it as a distinct
        // LlmCredentialSource so the status endpoint can render a
        // well-formed "unreadable" response instead of returning 500.
        try
        {
            // Tier 1: unit-scoped secret (subject to the Unit → Tenant inheritance
            // fall-through implemented by ComposedSecretResolver). We ask at unit
            // scope when a unit id is supplied so the resolver transparently
            // inherits from the tenant when the unit has no override; when no
            // unit is supplied we go straight to tenant scope.
            if (unitId.HasValue && unitId.Value != Guid.Empty)
            {
                var unitRef = new SecretRef(SecretScope.Unit, unitId.Value, secretName);
                var resolution = await _secretResolver.ResolveWithPathAsync(unitRef, cancellationToken);
                if (resolution.Value is { Length: > 0 } unitValue)
                {
                    var source = resolution.Path == SecretResolvePath.InheritedFromTenant
                        ? LlmCredentialSource.Tenant
                        : LlmCredentialSource.Unit;
                    return new LlmCredentialResolution(unitValue, source, secretName);
                }
            }
            else
            {
                // No unit in context — consult tenant-scoped secret directly.
                var tenantRef = new SecretRef(
                    SecretScope.Tenant,
                    _tenantContext.CurrentTenantId,
                    secretName);
                var resolution = await _secretResolver.ResolveWithPathAsync(tenantRef, cancellationToken);
                if (resolution.Value is { Length: > 0 } tenantValue)
                {
                    return new LlmCredentialResolution(tenantValue, LlmCredentialSource.Tenant, secretName);
                }
            }
        }
        catch (SecretUnreadableException ex)
        {
            _logger.LogWarning(
                ex,
                "LLM credential for provider {Provider} is stored but could not be decrypted; returning Unreadable.",
                providerId);
            return new LlmCredentialResolution(null, LlmCredentialSource.Unreadable, secretName);
        }

        _logger.LogDebug(
            "LLM credential for provider {Provider} not configured at unit or tenant scope; returning NotFound.",
            providerId);
        return new LlmCredentialResolution(null, LlmCredentialSource.NotFound, secretName);
    }

    private string? ResolveSecretName(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return null;
        }

        var runtime = _registry.Get(providerId);
        if (runtime is null)
        {
            return null;
        }

        var secretName = runtime.CredentialSecretName;
        return string.IsNullOrEmpty(secretName) ? null : secretName;
    }
}