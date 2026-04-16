// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.DependencyInjection;

using System.Collections.Generic;
using System.IO;

using Cvoya.Spring.Dapr.DependencyInjection;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Shouldly;

using Xunit;

/// <summary>
/// Exercises <see cref="DataProtectionServiceCollectionExtensions.AddCvoyaSpringDataProtection"/>
/// (issue #337). The key scenario is survival across service-provider
/// disposal: a fresh provider built against the same on-disk key ring must
/// unprotect a payload protected by an earlier provider.
/// </summary>
public class DataProtectionServiceCollectionExtensionsTests
{
    private static IConfiguration BuildConfiguration(string? keysPath)
    {
        var builder = new ConfigurationBuilder();
        if (!string.IsNullOrEmpty(keysPath))
        {
            builder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataProtection:KeysPath"] = keysPath,
            });
        }
        return builder.Build();
    }

    private static ServiceProvider BuildProvider(string? keysPath)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCvoyaSpringDataProtection(BuildConfiguration(keysPath));
        return services.BuildServiceProvider();
    }

    private static string CreateTempKeysDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"SpringDpKeys_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void AddCvoyaSpringDataProtection_NoKeysPath_RegistersDefaultProvider()
    {
        using var provider = BuildProvider(keysPath: null);

        var dataProtection = provider.GetService<IDataProtectionProvider>();

        dataProtection.ShouldNotBeNull();

        // Sanity: Protect/Unprotect round-trips without a keys path (uses
        // the ASP.NET Core default: ~/.aspnet/DataProtection-Keys on the
        // host filesystem). This covers unit tests and local `dotnet run`
        // loops that aren't bound to a container lifecycle.
        var protector = dataProtection.CreateProtector("test");
        var roundTripped = protector.Unprotect(protector.Protect("hello"));

        roundTripped.ShouldBe("hello");
    }

    [Fact]
    public void AddCvoyaSpringDataProtection_WithKeysPath_PersistsKeysToDirectory()
    {
        var keysPath = CreateTempKeysDirectory();
        try
        {
            using var provider = BuildProvider(keysPath);

            var dataProtection = provider.GetService<IDataProtectionProvider>();
            dataProtection.ShouldNotBeNull();

            // Round-trip through Protect/Unprotect — this forces the
            // key-ring initializer to create a key, which the file-system
            // persister then writes to disk.
            var protector = dataProtection.CreateProtector("secret-service");
            var original = "sensitive-payload";
            var protectedText = protector.Protect(original);
            protector.Unprotect(protectedText).ShouldBe(original);

            // A key-*.xml file must have landed in the configured directory.
            Directory.GetFiles(keysPath, "key-*.xml").ShouldNotBeEmpty();
        }
        finally
        {
            if (Directory.Exists(keysPath))
            {
                Directory.Delete(keysPath, recursive: true);
            }
        }
    }

    [Fact]
    public void AddCvoyaSpringDataProtection_MissingKeysDirectory_IsCreated()
    {
        var keysPath = Path.Combine(
            Path.GetTempPath(),
            $"SpringDpKeys_NoMkdir_{Guid.NewGuid():N}");
        try
        {
            Directory.Exists(keysPath).ShouldBeFalse();

            using var provider = BuildProvider(keysPath);

            // Resolving the provider (and protecting once) should create
            // the directory — mirrors the first-boot case where a bind
            // mount target exists but the path inside the container has
            // never been written.
            var dataProtection = provider.GetRequiredService<IDataProtectionProvider>();
            dataProtection.CreateProtector("bootstrap").Protect("x");

            Directory.Exists(keysPath).ShouldBeTrue();
        }
        finally
        {
            if (Directory.Exists(keysPath))
            {
                Directory.Delete(keysPath, recursive: true);
            }
        }
    }

    /// <summary>
    /// The bug we are fixing: the default <c>FileSystemXmlRepository</c>
    /// keys live inside the container and vanish on every rebuild, which
    /// silently invalidates anything the previous process protected.
    /// Prove the fix by round-tripping a payload across two independent
    /// service providers sharing one on-disk keys directory.
    /// </summary>
    [Fact]
    public void AddCvoyaSpringDataProtection_KeyRingSurvivesServiceProviderDisposal()
    {
        var keysPath = CreateTempKeysDirectory();
        try
        {
            // Provider A: protect a payload, then dispose.
            string protectedText;
            using (var providerA = BuildProvider(keysPath))
            {
                var protector = providerA
                    .GetRequiredService<IDataProtectionProvider>()
                    .CreateProtector("cross-restart");
                protectedText = protector.Protect("survives-rebuild");
            }

            // Provider B: fresh container, same keys directory, same
            // application name (set by AddCvoyaSpringDataProtection). The
            // key ring is loaded from disk so the ciphertext is still
            // decryptable.
            using var providerB = BuildProvider(keysPath);
            var unprotected = providerB
                .GetRequiredService<IDataProtectionProvider>()
                .CreateProtector("cross-restart")
                .Unprotect(protectedText);

            unprotected.ShouldBe("survives-rebuild");
        }
        finally
        {
            if (Directory.Exists(keysPath))
            {
                Directory.Delete(keysPath, recursive: true);
            }
        }
    }

    /// <summary>
    /// The private cloud host registers its own <c>AddDataProtection()</c>
    /// chain (for example <c>.ProtectKeysWithAzureKeyVault(...)</c>) BEFORE
    /// calling <see cref="DataProtectionServiceCollectionExtensions.AddCvoyaSpringDataProtection"/>.
    /// Our extension must detect that registration and stay out of the way
    /// — otherwise it would overwrite the cloud persister with the OSS
    /// file-system default.
    /// </summary>
    [Fact]
    public void AddCvoyaSpringDataProtection_ExistingRegistration_IsPreserved()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // Private-cloud-style registration: a file-system persister in a
        // pre-chosen directory with a distinctive application name. The
        // OSS extension should NOT replace or augment this.
        var preExistingPath = CreateTempKeysDirectory();
        try
        {
            services.AddDataProtection()
                .SetApplicationName("Cvoya.Spring.Cloud")
                .PersistKeysToFileSystem(new DirectoryInfo(preExistingPath));

            var descriptorCountBefore = services.Count;

            // Point the OSS extension at a DIFFERENT directory; if the
            // extension honored it, that's the bug we're guarding against.
            var shouldBeIgnored = CreateTempKeysDirectory();
            try
            {
                services.AddCvoyaSpringDataProtection(BuildConfiguration(shouldBeIgnored));

                // No new services registered — the extension short-circuits.
                services.Count.ShouldBe(descriptorCountBefore);

                using var provider = services.BuildServiceProvider();
                var protector = provider
                    .GetRequiredService<IDataProtectionProvider>()
                    .CreateProtector("p");
                protector.Unprotect(protector.Protect("roundtrip"))
                    .ShouldBe("roundtrip");

                // Keys landed in the pre-existing path, NOT the one we
                // passed via configuration.
                Directory.GetFiles(preExistingPath, "key-*.xml").ShouldNotBeEmpty();
                Directory.GetFiles(shouldBeIgnored, "key-*.xml").ShouldBeEmpty();
            }
            finally
            {
                if (Directory.Exists(shouldBeIgnored))
                {
                    Directory.Delete(shouldBeIgnored, recursive: true);
                }
            }
        }
        finally
        {
            if (Directory.Exists(preExistingPath))
            {
                Directory.Delete(preExistingPath, recursive: true);
            }
        }
    }
}