// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Services;

using Cvoya.Spring.Dapr.Skills;
using Cvoya.Spring.Host.Api.Auth;
using Cvoya.Spring.Manifest;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

/// <summary>
/// DI registration helpers for the API host's own services (unit creation
/// pipeline, package catalog). Uses <c>TryAdd*</c> so the private cloud repo
/// can register tenant-scoped replacements ahead of the API host.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the unit creation service and the file-system backed package
    /// catalog. The packages root is read from <c>Packages:Root</c> (falling
    /// back to <c>SPRING_PACKAGES_ROOT</c> via standard configuration binding).
    /// </summary>
    public static IServiceCollection AddCvoyaSpringApiServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.TryAddScoped<IUnitCreationService, UnitCreationService>();
        services.TryAddScoped<IPackageArtefactActivator, DefaultPackageArtefactActivator>();
        services.TryAddScoped<IPackageInstallService, PackageInstallService>();
        services.TryAddScoped<IPackageExportService, PackageExportService>();
        services.TryAddScoped<IAuthenticatedCallerAccessor, AuthenticatedCallerAccessor>();

        // Participant display-name resolution (#1485). Registered as scoped so the
        // per-request cache in ParticipantDisplayNameResolver stays request-bounded.
        // TryAdd so the private cloud repo can register a tenant-aware variant ahead of
        // this call.
        services.TryAddScoped<IParticipantDisplayNameResolver, ParticipantDisplayNameResolver>();

        // OSS default: grant every authenticated caller all three platform
        // roles (PlatformOperator / TenantOperator / TenantUser). The cloud
        // overlay registers its own IRoleClaimSource via TryAddSingleton
        // ahead of this call to scope the granted subset per identity.
        services.TryAddSingleton<IRoleClaimSource, OssAllRolesClaimSource>();

        var configuredRoot = configuration["Packages:Root"]
            ?? System.Environment.GetEnvironmentVariable("SPRING_PACKAGES_ROOT");

        var options = new PackageCatalogOptions
        {
            Root = configuredRoot ?? DiscoverPackagesRoot(),
        };
        services.TryAddSingleton(options);
        services.TryAddSingleton<FileSystemPackageCatalogService>();
        services.TryAddSingleton<IPackageCatalogService>(
            sp => sp.GetRequiredService<FileSystemPackageCatalogService>());
        services.TryAddSingleton<IPackageCatalogProvider>(
            sp => sp.GetRequiredService<FileSystemPackageCatalogService>());

        // Share the same packages root with the skill-bundle resolver when
        // the operator has not set 'Skills:PackagesRoot' explicitly. Lets a
        // single `Packages:Root` (or the auto-discovered sibling) serve both
        // the unit-template catalog and the skill-bundle resolver. The
        // PostConfigure resolves PackageCatalogOptions from the container at
        // bind time so tests that swap the options instance (see
        // UnitCreationEndpointTests) are honoured.
        //
        // Note: AddCvoyaSpringDapr also registers a configuration-level
        // fallback that maps `Packages:Root` → SkillBundleOptions.PackagesRoot
        // so the Worker host (which owns default-tenant bootstrap; see #969)
        // gets the bridge without depending on Host.Api. This post-configure
        // layers on top to cover the API-only advantages: the
        // DiscoverPackagesRoot() auto-walk and test overrides of
        // PackageCatalogOptions.Root.
        services.AddSingleton<IPostConfigureOptions<SkillBundleOptions>>(sp =>
            new SkillBundlePackagesRootPostConfigure(
                sp.GetRequiredService<PackageCatalogOptions>()));

        return services;
    }

    /// <summary>
    /// Fills in <see cref="SkillBundleOptions.PackagesRoot"/> from the shared
    /// <see cref="PackageCatalogOptions.Root"/> when the operator has not set
    /// it explicitly via configuration.
    /// </summary>
    private sealed class SkillBundlePackagesRootPostConfigure(PackageCatalogOptions shared)
        : IPostConfigureOptions<SkillBundleOptions>
    {
        public void PostConfigure(string? name, SkillBundleOptions options)
        {
            if (string.IsNullOrWhiteSpace(options.PackagesRoot))
            {
                options.PackagesRoot = shared.Root;
            }
        }
    }

    /// <summary>
    /// Walks upward from the current working directory looking for a
    /// <c>packages/</c> sibling — useful for developers running the API from
    /// <c>src/Cvoya.Spring.Host.Api</c> during <c>dotnet run</c>.
    /// Returns <c>null</c> when no such directory can be found, in which case
    /// the catalog is simply empty at runtime.
    /// </summary>
    private static string? DiscoverPackagesRoot()
    {
        var current = new System.IO.DirectoryInfo(System.IO.Directory.GetCurrentDirectory());
        for (var depth = 0; depth < 6 && current is not null; depth++, current = current.Parent)
        {
            var candidate = System.IO.Path.Combine(current.FullName, "packages");
            if (System.IO.Directory.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }
}