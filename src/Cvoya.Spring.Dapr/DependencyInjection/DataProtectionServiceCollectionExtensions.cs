// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.DependencyInjection;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// ASP.NET Core DataProtection registration for Spring Voyage hosts.
/// </summary>
/// <remarks>
/// <para>
/// DataProtection is the default encryption primitive for authentication
/// cookies, bearer tokens, anti-forgery tokens, and anything routed through
/// <c>IDataProtector.Protect(...)</c> — most notably the OAuth session store
/// that persists connector refresh tokens. Without an explicit configuration
/// the framework falls back to the per-container
/// <c>/home/app/.aspnet/DataProtection-Keys</c> directory and logs the
/// ephemeral-keys warning (<c>FileSystemXmlRepository[60]</c>). Every
/// container rebuild regenerates the key ring, silently invalidating
/// anything that was previously protected (see issue #337).
/// </para>
/// <para>
/// The OSS platform ships a minimal registration that:
/// <list type="bullet">
///   <item>
///     Sets a stable <c>ApplicationName</c> (<c>Cvoya.Spring</c>) so the API
///     and Worker hosts — and any future replicas — share a single key ring.
///   </item>
///   <item>
///     Persists keys to <c>DataProtection:KeysPath</c> when configured. The
///     <c>deploy.sh</c> Podman topology bind-mounts a named volume at that
///     path so keys survive <c>./deploy.sh restart</c> and image rebuilds.
///   </item>
///   <item>
///     Falls through to the ASP.NET Core default when the path is unset —
///     acceptable for unit tests and local <c>dotnet run</c> loops that are
///     not paired with a container lifecycle.
///   </item>
/// </list>
/// </para>
/// <para>
/// Encryption-at-rest for the key ring is intentionally NOT configured here.
/// That is a deployment concern: the private cloud host layers
/// <c>.ProtectKeysWithAzureKeyVault(...)</c> (or equivalent) and typically
/// swaps the file-system persister for Redis / Postgres via its own
/// <c>AddDataProtection()</c> chain. To stay out of the cloud host's way
/// this extension is a no-op when an <see cref="IDataProtectionProvider"/>
/// registration already exists in the <see cref="IServiceCollection"/>,
/// letting the cloud host run its richer configuration first.
/// </para>
/// </remarks>
public static class DataProtectionServiceCollectionExtensions
{
    /// <summary>
    /// Configuration section bound for DataProtection options.
    /// </summary>
    public const string ConfigurationSection = "DataProtection";

    /// <summary>
    /// Configuration key for the on-disk key-ring directory.
    /// </summary>
    public const string KeysPathKey = "KeysPath";

    /// <summary>
    /// Shared ASP.NET Core DataProtection application name for the Spring Voyage
    /// platform. Both API and Worker hosts use the same name so cookies,
    /// anti-forgery tokens, and anything else protected by one host can be
    /// unprotected by the other.
    /// </summary>
    public const string ApplicationName = "Cvoya.Spring";

    /// <summary>
    /// Registers ASP.NET Core DataProtection with a shared application name
    /// and — when <c>DataProtection:KeysPath</c> is configured — persists the
    /// key ring to that directory so keys survive container rebuilds.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="configuration">Configuration root used to bind <c>DataProtection:KeysPath</c>.</param>
    /// <returns>The same service collection for chaining.</returns>
    /// <remarks>
    /// <para>
    /// If an <see cref="IDataProtectionProvider"/> is already registered
    /// (e.g. the private cloud host pre-registered
    /// <c>AddDataProtection().ProtectKeysWithAzureKeyVault(...)</c>), this
    /// method is a no-op: it does not overwrite the existing chain.
    /// </para>
    /// <para>
    /// When the keys path is set, the directory is created if missing so
    /// the first startup does not crash because the bind-mount target is
    /// empty.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddCvoyaSpringDataProtection(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Respect a pre-existing registration. The private cloud repo may
        // have already called AddDataProtection() with its own persister
        // (Redis, Postgres, Azure Key Vault, ...) and encryption-at-rest
        // chain. Re-calling AddDataProtection() here would replace the
        // default persister silently and undo that work.
        if (services.Any(d => d.ServiceType == typeof(IDataProtectionProvider)))
        {
            return services;
        }

        var keysPath = configuration.GetSection(ConfigurationSection)[KeysPathKey];

        var builder = services.AddDataProtection()
            .SetApplicationName(ApplicationName);

        if (!string.IsNullOrWhiteSpace(keysPath))
        {
            Directory.CreateDirectory(keysPath);
            builder.PersistKeysToFileSystem(new DirectoryInfo(keysPath));
        }

        return services;
    }
}