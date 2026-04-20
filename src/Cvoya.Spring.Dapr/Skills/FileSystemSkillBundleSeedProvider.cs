// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Skills;

using System.IO;

using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Core.Tenancy;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Tenant seed provider that walks the on-disk packages root and binds
/// every discovered bundle to the bootstrapped tenant via
/// <see cref="ITenantSkillBundleBindingService"/>. Bindings default to
/// <c>enabled=true</c> so a fresh OSS deployment surfaces every shipped
/// bundle to the default tenant.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Idempotency.</strong> <see cref="ITenantSkillBundleBindingService.BindAsync"/>
/// is upsert-shaped — re-running the provider on an existing deployment
/// refreshes no persistent state. Skipped binds are logged at
/// <c>Debug</c> level by the service; the provider itself logs every
/// discovered package at <c>Information</c> so operators can confirm the
/// file-system layout matches what the manifest layer will look for.
/// </para>
/// <para>
/// <strong>Failure mode.</strong> A misconfigured packages root
/// (missing directory, no <c>Skills:PackagesRoot</c> set) is logged at
/// <c>Warning</c> level and the provider returns without throwing — an
/// OSS deployment that does not ship bundles must still bootstrap.
/// </para>
/// <para>
/// <strong>DI lifecycle.</strong> Registered as a singleton; opens a
/// child DI scope per bootstrap pass so the scoped
/// <see cref="ITenantSkillBundleBindingService"/> (which holds a
/// <see cref="Data.SpringDbContext"/>) resolves correctly.
/// </para>
/// </remarks>
public class FileSystemSkillBundleSeedProvider(
    IOptions<SkillBundleOptions> options,
    IServiceScopeFactory scopeFactory,
    ILogger<FileSystemSkillBundleSeedProvider> logger) : ITenantSeedProvider
{
    private readonly SkillBundleOptions _options = options.Value;

    /// <summary>
    /// Stable id used in bootstrap audit logs.
    /// </summary>
    public string Id => "skill-bundles";

    /// <summary>
    /// Runs early in the bootstrap pass. Bundles are platform
    /// infrastructure (the manifest references them by name); seeding
    /// them ahead of, say, default policies keeps the log readable
    /// when later providers reference a bundle by id.
    /// </summary>
    public int Priority => 10;

    /// <inheritdoc />
    public async Task ApplySeedsAsync(string tenantId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        var root = _options.PackagesRoot;
        if (string.IsNullOrWhiteSpace(root))
        {
            logger.LogWarning(
                "Tenant '{TenantId}' skill-bundle seed: 'Skills:PackagesRoot' is not configured; skipping enumeration.",
                tenantId);
            return;
        }

        if (!Directory.Exists(root))
        {
            logger.LogWarning(
                "Tenant '{TenantId}' skill-bundle seed: configured packages root '{PackagesRoot}' does not exist; skipping enumeration.",
                tenantId, root);
            return;
        }

        var discovered = new List<(string BundleId, int SkillCount)>();
        foreach (var packageDir in Directory.EnumerateDirectories(root))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var packageName = Path.GetFileName(packageDir);
            var skillsDir = Path.Combine(packageDir, "skills");
            var skillCount = Directory.Exists(skillsDir)
                ? Directory.EnumerateFiles(skillsDir, "*.md").Count()
                : 0;

            logger.LogInformation(
                "Tenant '{TenantId}' skill-bundle seed: discovered package '{Package}' with {SkillCount} skill(s).",
                tenantId, packageName, skillCount);

            discovered.Add((packageName, skillCount));
        }

        if (discovered.Count == 0)
        {
            logger.LogInformation(
                "Tenant '{TenantId}' skill-bundle seed: no packages under '{PackagesRoot}'; nothing to bind.",
                tenantId, root);
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var bindingService = scope.ServiceProvider
            .GetRequiredService<ITenantSkillBundleBindingService>();

        foreach (var (bundleId, _) in discovered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await bindingService.BindAsync(bundleId, enabled: true, cancellationToken)
                .ConfigureAwait(false);
        }

        logger.LogInformation(
            "Tenant '{TenantId}' skill-bundle seed: bound {PackageCount} package(s) from '{PackagesRoot}'.",
            tenantId, discovered.Count, root);
    }
}