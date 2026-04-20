// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Skills;

using System.IO;

using Cvoya.Spring.Core.Tenancy;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Tenant seed provider adapter that lets the OSS file-system skill
/// bundle resolver participate in the default-tenant bootstrap pass.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What this seeds.</strong> Today the OSS skill bundle layer
/// has no per-tenant install table — bundles are read from disk on
/// demand by <see cref="FileSystemSkillBundleResolver"/>, and units
/// pick the bundles they want via the manifest. There is therefore
/// nothing for this provider to upsert. Its real job is to enumerate
/// the on-disk packages root and emit one <c>Information</c>-level
/// log entry per discovered package so operators can confirm at boot
/// time that the file-system layout matches what the manifest layer
/// will look for. The skill-bundle tenant binding table that would
/// give this provider real upsert work is a Phase 2 sub-issue (see
/// the comment in #676's "Out of scope" section); when it lands the
/// upsert logic slots into this provider without changing the
/// <see cref="ITenantSeedProvider"/> contract or the bootstrap caller.
/// </para>
/// <para>
/// <strong>Idempotency.</strong> Pure read-only enumeration. Re-runs
/// produce the same log lines and never mutate state.
/// </para>
/// <para>
/// <strong>Failure mode.</strong> A misconfigured packages root
/// (missing directory, no <c>Skills:PackagesRoot</c> set) is logged
/// at <c>Warning</c> level and the provider returns without throwing
/// — an OSS deployment that does not ship bundles must still be able
/// to bootstrap. The
/// <see cref="FileSystemSkillBundleResolver"/> itself throws on a
/// missing root the first time a unit asks for a bundle, which is the
/// correct fail-loud surface for the operator-driven "install this
/// unit" flow.
/// </para>
/// </remarks>
public class FileSystemSkillBundleSeedProvider(
    IOptions<SkillBundleOptions> options,
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
    public Task ApplySeedsAsync(string tenantId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        var root = _options.PackagesRoot;
        if (string.IsNullOrWhiteSpace(root))
        {
            logger.LogWarning(
                "Tenant '{TenantId}' skill-bundle seed: 'Skills:PackagesRoot' is not configured; skipping enumeration.",
                tenantId);
            return Task.CompletedTask;
        }

        if (!Directory.Exists(root))
        {
            logger.LogWarning(
                "Tenant '{TenantId}' skill-bundle seed: configured packages root '{PackagesRoot}' does not exist; skipping enumeration.",
                tenantId, root);
            return Task.CompletedTask;
        }

        var packageCount = 0;
        foreach (var packageDir in Directory.EnumerateDirectories(root))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var packageName = Path.GetFileName(packageDir);
            var skillsDir = Path.Combine(packageDir, "skills");
            var skillCount = 0;
            if (Directory.Exists(skillsDir))
            {
                skillCount = Directory.EnumerateFiles(skillsDir, "*.md").Count();
            }

            logger.LogInformation(
                "Tenant '{TenantId}' skill-bundle seed: discovered package '{Package}' with {SkillCount} skill(s).",
                tenantId, packageName, skillCount);

            packageCount++;
        }

        logger.LogInformation(
            "Tenant '{TenantId}' skill-bundle seed: enumerated {PackageCount} package(s) under '{PackagesRoot}'.",
            tenantId, packageCount, root);

        return Task.CompletedTask;
    }
}