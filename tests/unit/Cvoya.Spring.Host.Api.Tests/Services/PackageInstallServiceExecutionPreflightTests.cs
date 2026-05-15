// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Services;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Core.Catalog;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Secrets;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Host.Api.Services;
using Cvoya.Spring.Manifest;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Pre-flight tests for the package-level <c>execution:</c> inheritance
/// (#1679). The tests live at the
/// <see cref="IPackageInstallService.InstallAsync"/> seam so they
/// exercise the same code path the HTTP endpoint takes — the resolver
/// runs after target resolution but before any DB writes, so the test
/// surface stays small (in-memory <see cref="SpringDbContext"/>, a
/// substituted <see cref="IDirectoryService"/> and
/// <see cref="IPackageArtefactActivator"/>).
/// </summary>
public class PackageInstallServiceExecutionPreflightTests
{
    [Fact]
    public async Task InstallAsync_PackageImage_MemberMissingImage_Inherits_NoMissing()
    {
        // Happy path: package declares execution.image; member declares no
        // execution block; the resolver merges and the activator sees the
        // inherited image. The activator substitution in this fixture
        // captures the resolved defaults so the assertion is on what the
        // activator was handed, not on Phase-2 side-effects.
        using var pkg = await BuildPackageAsync(
            packageYaml: """
                apiVersion: spring.voyage/v1
                kind: Package
                name: pkg-1679-happy
                description: x
                version: 1.0.0
                execution:
                  image: ghcr.io/example/agents:latest
                """,
            unitFiles: new[]
            {
                ("alpha.yaml", """
                    apiVersion: spring.voyage/v1
                    kind: Unit
                    name: alpha
                    description: x
                    """),
            });

        var (service, capturingActivator) = BuildService();

        await service.InstallAsync(
            new[] { new InstallTarget(
                PackageName: "pkg-1679-happy",
                Inputs: new Dictionary<string, string>(),
                OriginalYaml: pkg.PackageYaml,
                PackageRoot: pkg.PackageRoot) },
            TestContext.Current.CancellationToken);

        capturingActivator.Captured.ShouldContainKey("alpha");
        capturingActivator.Captured["alpha"]!.Image.ShouldBe("ghcr.io/example/agents:latest");
    }

    [Fact]
    public async Task InstallAsync_NoImageAnywhere_ThrowsConfigurationIncomplete()
    {
        // Pre-flight rejection: neither side declares execution.image for
        // the inheriting member. The resolver surfaces a structured list
        // before any DB writes; the install endpoint converts it to 400.
        using var pkg = await BuildPackageAsync(
            packageYaml: """
                apiVersion: spring.voyage/v1
                kind: Package
                name: pkg-1679-missing
                description: x
                version: 1.0.0
                """,
            unitFiles: new[]
            {
                ("alpha.yaml", """
                    apiVersion: spring.voyage/v1
                    kind: Unit
                    name: alpha
                    description: x
                    """),
            });

        var (service, _) = BuildService();

        var ex = await Should.ThrowAsync<ExecutionConfigurationsMissingException>(async () =>
            await service.InstallAsync(
                new[] { new InstallTarget(
                    PackageName: "pkg-1679-missing",
                    Inputs: new Dictionary<string, string>(),
                    OriginalYaml: pkg.PackageYaml,
                    PackageRoot: pkg.PackageRoot) },
                TestContext.Current.CancellationToken));

        ex.Missing.Count.ShouldBe(1);
        // Resolved artefact names come from the package manifest's
        // content list (the bare name), not from the unit YAML's
        // `name:` header — that's the display name and not the
        // identity the resolver / install pipeline keys off.
        ex.Missing[0].UnitName.ShouldBe("alpha");
        ex.Missing[0].Field.ShouldBe("image");
        ex.Message.ShouldContain("declare execution.image");
    }

    [Fact]
    public async Task InstallAsync_MemberOverrideImage_PackageModel_FieldwiseMerge()
    {
        // Field-wise merge: the package supplies model; the member
        // supplies its own image. The activator should see the merged
        // execution defaults — member image plus inherited model.
        using var pkg = await BuildPackageAsync(
            packageYaml: """
                apiVersion: spring.voyage/v1
                kind: Package
                name: pkg-1679-merge
                description: x
                version: 1.0.0
                execution:
                  image: ghcr.io/example/pkg:latest
                  model: claude-opus-4-7
                """,
            unitFiles: new[]
            {
                ("alpha.yaml", """
                    apiVersion: spring.voyage/v1
                    kind: Unit
                    name: alpha
                    description: x
                    execution:
                      image: ghcr.io/example/alpha:latest
                    """),
            });

        var (service, capturingActivator) = BuildService();

        await service.InstallAsync(
            new[] { new InstallTarget(
                PackageName: "pkg-1679-merge",
                Inputs: new Dictionary<string, string>(),
                OriginalYaml: pkg.PackageYaml,
                PackageRoot: pkg.PackageRoot) },
            TestContext.Current.CancellationToken);

        var captured = capturingActivator.Captured["alpha"]!;
        captured.Image.ShouldBe("ghcr.io/example/alpha:latest");  // member wins
        captured.Model.ShouldBe("claude-opus-4-7");                // package fills the gap
    }

    // ---- Helpers --------------------------------------------------------

    /// <summary>
    /// Recording <see cref="IPackageArtefactActivator"/>: captures the
    /// per-unit resolved execution defaults the install service forwards.
    /// The Phase-2 side-effects (creation, directory writes) are
    /// out-of-scope for the pre-flight tests so the substitute returns
    /// without doing any work.
    /// </summary>
    private sealed class CapturingActivator : IPackageArtefactActivator
    {
        public Dictionary<string, ResolvedExecutionDefaults?> Captured { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public Task ActivateAsync(
            string packageName,
            ResolvedArtefact artefact,
            Guid installId,
            LocalSymbolMap symbolMap,
            IReadOnlyDictionary<string, ConnectorBinding>? connectorBindings = null,
            ResolvedExecutionDefaults? executionDefaults = null,
            string? displayNameOverride = null,
            CancellationToken cancellationToken = default)
        {
            if (artefact.Kind == ArtefactKind.Unit)
            {
                Captured[artefact.Name] = executionDefaults;
            }
            return Task.CompletedTask;
        }
    }

    private static (PackageInstallService Service, CapturingActivator Activator) BuildService()
    {
        var dbName = $"sv-1679-{Guid.NewGuid():N}";
        var services = new ServiceCollection();
        services.AddDbContext<SpringDbContext>(opt => opt
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w =>
                w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning)));

        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
        var directory = Substitute.For<IDirectoryService>();
        var activator = new CapturingActivator();

        // The execution-preflight tests don't exercise credential
        // resolution (units in these fixtures don't declare an `ai:`
        // block), so a no-op resolver returning NotFound is fine — the
        // preflight short-circuits before consulting it. Substituted
        // secret store / registry / tenant context likewise stay
        // unused.
        var credentialResolver = Substitute.For<ILlmCredentialResolver>();
        credentialResolver
            .ResolveAsync(Arg.Any<string>(), Arg.Any<AuthMethod>(), Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(new LlmCredentialResolution(null, LlmCredentialSource.NotFound, string.Empty)));
        var tenantContext = Substitute.For<ITenantContext>();
        tenantContext.CurrentTenantId.Returns(Cvoya.Spring.Core.Tenancy.OssTenantIds.Default);

        var service = new PackageInstallService(
            scopeFactory,
            directory,
            activator,
            Cvoya.Spring.RuntimeCatalog.RuntimeCatalogLoader.LoadEmbedded(),
            credentialResolver,
            Substitute.For<ISecretStore>(),
            Substitute.For<ISecretRegistry>(),
            tenantContext,
            NullLogger<PackageInstallService>.Instance);

        return (service, activator);
    }

    /// <summary>
    /// Builds a package tree in the ADR-0043 recursive shape. Each
    /// <paramref name="unitFiles"/> tuple is treated as
    /// <c>(&lt;short-name&gt;.yaml, &lt;content&gt;)</c>; the
    /// <c>.yaml</c> extension is stripped to derive the folder name
    /// (<c>units/&lt;short-name&gt;/package.yaml</c>). The unit's
    /// inner manifest's <c>name:</c> field must match the folder
    /// name (ADR-0043 §8 — <c>ArtefactFolderNameMismatch</c>).
    /// </summary>
    private static async Task<TempPackage> BuildPackageAsync(
        string packageYaml,
        (string Filename, string Content)[] unitFiles)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "sv-1679-pre-" + Path.GetRandomFileName());
        Directory.CreateDirectory(tempRoot);
        var unitsDir = Path.Combine(tempRoot, "units");
        Directory.CreateDirectory(unitsDir);

        var packagePath = Path.Combine(tempRoot, "package.yaml");
        await File.WriteAllTextAsync(packagePath, packageYaml);
        foreach (var (filename, content) in unitFiles)
        {
            var folderName = Path.GetFileNameWithoutExtension(filename);
            var unitDir = Path.Combine(unitsDir, folderName);
            Directory.CreateDirectory(unitDir);
            await File.WriteAllTextAsync(Path.Combine(unitDir, "package.yaml"), content);
        }

        return new TempPackage(tempRoot, packageYaml);
    }

    private sealed class TempPackage : IDisposable
    {
        public string PackageRoot { get; }
        public string PackageYaml { get; }

        public TempPackage(string packageRoot, string packageYaml)
        {
            PackageRoot = packageRoot;
            PackageYaml = packageYaml;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(PackageRoot))
                {
                    Directory.Delete(PackageRoot, recursive: true);
                }
            }
            catch { /* best effort */ }
        }
    }
}
