// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

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
/// The canonical secret name is derived from the provider id:
/// <c>claude</c> → <c>anthropic-api-key</c>, <c>openai</c> →
/// <c>openai-api-key</c>, <c>google</c> → <c>google-api-key</c>. The
/// names were chosen to match the documentation in
/// <c>docs/guide/secrets.md</c> and the tenant-defaults portal panel's
/// labels so operators see the same string everywhere. Unknown provider
/// ids return <see cref="LlmCredentialSource.NotFound"/>.
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
    private readonly ISecretResolver _secretResolver;
    private readonly ITenantContext _tenantContext;
    private readonly ILogger<LlmCredentialResolver> _logger;

    /// <summary>
    /// Creates a new <see cref="LlmCredentialResolver"/>.
    /// </summary>
    public LlmCredentialResolver(
        ISecretResolver secretResolver,
        ITenantContext tenantContext,
        ILogger<LlmCredentialResolver> logger)
    {
        _secretResolver = secretResolver;
        _tenantContext = tenantContext;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<LlmCredentialResolution> ResolveAsync(
        string providerId,
        string? unitName,
        CancellationToken cancellationToken = default)
    {
        var descriptor = DescriptorFor(providerId);
        if (descriptor is null)
        {
            return new LlmCredentialResolution(null, LlmCredentialSource.NotFound, string.Empty);
        }

        // Tier 1: unit-scoped secret (subject to the Unit → Tenant inheritance
        // fall-through implemented by ComposedSecretResolver). We ask at unit
        // scope when a unit name is supplied so the resolver transparently
        // inherits from the tenant when the unit has no override; when no
        // unit is supplied we go straight to tenant scope.
        if (!string.IsNullOrWhiteSpace(unitName))
        {
            var unitRef = new SecretRef(SecretScope.Unit, unitName!, descriptor.SecretName);
            var resolution = await _secretResolver.ResolveWithPathAsync(unitRef, cancellationToken);
            if (resolution.Value is { Length: > 0 } unitValue)
            {
                var source = resolution.Path == SecretResolvePath.InheritedFromTenant
                    ? LlmCredentialSource.Tenant
                    : LlmCredentialSource.Unit;
                return new LlmCredentialResolution(unitValue, source, descriptor.SecretName);
            }
        }
        else
        {
            // No unit in context — consult tenant-scoped secret directly.
            var tenantRef = new SecretRef(
                SecretScope.Tenant,
                _tenantContext.CurrentTenantId,
                descriptor.SecretName);
            var resolution = await _secretResolver.ResolveWithPathAsync(tenantRef, cancellationToken);
            if (resolution.Value is { Length: > 0 } tenantValue)
            {
                return new LlmCredentialResolution(tenantValue, LlmCredentialSource.Tenant, descriptor.SecretName);
            }
        }

        _logger.LogDebug(
            "LLM credential for provider {Provider} not configured at unit or tenant scope; returning NotFound.",
            providerId);
        return new LlmCredentialResolution(null, LlmCredentialSource.NotFound, descriptor.SecretName);
    }

    /// <summary>
    /// The canonical provider → secret-name mapping. Exposed internally
    /// for tests and the portal documentation so the Tenant defaults
    /// panel can show operators the expected names.
    /// </summary>
    internal static ProviderDescriptor? DescriptorFor(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return null;
        }

        return providerId.Trim().ToLowerInvariant() switch
        {
            "claude" or "anthropic" => new ProviderDescriptor("anthropic-api-key"),
            "openai" => new ProviderDescriptor("openai-api-key"),
            "google" or "gemini" or "googleai" => new ProviderDescriptor("google-api-key"),
            _ => null,
        };
    }

    /// <summary>
    /// Describes a provider's canonical secret name.
    /// </summary>
    internal sealed record ProviderDescriptor(string SecretName);
}