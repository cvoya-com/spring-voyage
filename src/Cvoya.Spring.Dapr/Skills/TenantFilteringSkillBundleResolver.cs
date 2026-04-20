// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Skills;

using Cvoya.Spring.Core.Skills;

using Microsoft.Extensions.Logging;

/// <summary>
/// <see cref="ISkillBundleResolver"/> decorator that checks the
/// requested package against the current tenant's
/// <see cref="ITenantSkillBundleBindingService"/> bindings before
/// delegating to the underlying file-system resolver. Bundles without a
/// binding — or with <c>enabled=false</c> — surface as
/// <see cref="SkillBundlePackageNotFoundException"/> so the caller
/// cannot distinguish an unbound bundle from a missing one.
/// </summary>
/// <remarks>
/// Scoped because <see cref="ITenantSkillBundleBindingService"/> is
/// scoped (holds <see cref="Data.SpringDbContext"/>). The inner
/// <see cref="FileSystemSkillBundleResolver"/> stays singleton and is
/// injected directly so its in-memory cache survives request boundaries.
/// </remarks>
public sealed class TenantFilteringSkillBundleResolver(
    FileSystemSkillBundleResolver inner,
    ITenantSkillBundleBindingService bindingService,
    ILogger<TenantFilteringSkillBundleResolver> logger) : ISkillBundleResolver
{
    /// <inheritdoc />
    public async Task<SkillBundle> ResolveAsync(
        SkillBundleReference reference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);

        var bundleId = NormalisePackageId(reference.Package);
        var binding = await bindingService.GetAsync(bundleId, cancellationToken)
            .ConfigureAwait(false);
        if (binding is null || !binding.Enabled)
        {
            logger.LogWarning(
                "Skill-bundle resolve: package '{Package}' is not bound to the current tenant or is disabled; refusing to resolve.",
                reference.Package);
            throw new SkillBundlePackageNotFoundException(
                reference.Package,
                "(not bound to current tenant — run the default-tenant bootstrap or V2.1 CLI to enable)");
        }

        return await inner.ResolveAsync(reference, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Mirrors <see cref="FileSystemSkillBundleResolver"/>'s prefix
    /// normalisation so a manifest entry like
    /// <c>spring-voyage/software-engineering</c> looks up the binding
    /// keyed on the package directory name <c>software-engineering</c>.
    /// Scans for any namespace prefix ending in <c>/</c>; unprefixed
    /// names are returned untouched.
    /// </summary>
    private static string NormalisePackageId(string packageName)
    {
        var slash = packageName.IndexOf('/');
        return slash >= 0 ? packageName[(slash + 1)..] : packageName;
    }
}