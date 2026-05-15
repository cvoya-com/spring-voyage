// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Services;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Core.Catalog;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Secrets;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Host.Api.Services;
using Cvoya.Spring.Manifest;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for ADR-0043 §6 — the install pipeline's <c>--into &lt;unit&gt;</c>
/// scope-binding plumbing. The resolved-package shape and the activator
/// are substituted (the activator stays a no-op so the test focuses on
/// the binding step itself).
/// </summary>
public class PackageInstallServiceIntoUnitTests
{
    [Fact]
    public async Task InstallAsync_IntoTenant_Explicit_Accepted()
    {
        // Explicit `--into tenant` resolves to the same path the default
        // (no `--into` flag) takes — top-level artefacts bind to the
        // tenant. The install service does NOT re-bind to a parent unit;
        // the activator's regular tenant-edge logic handles top-level
        // artefacts. This test verifies the explicit "tenant" literal
        // parses without throwing.
        using var pkg = BuildPackageWithOneUnit(name: "pkg-into-tenant");

        var (service, scopeFactory, tenantId) = BuildService();

        var result = await service.InstallAsync(
            new[] { new InstallTarget(
                PackageName: "pkg-into-tenant",
                Inputs: new Dictionary<string, string>(),
                OriginalYaml: pkg.PackageYaml,
                PackageRoot: pkg.PackageRoot,
                IntoUnit: "tenant") },
            TestContext.Current.CancellationToken);

        result.PackageResults.Single().Status.ShouldBe(PackageInstallOutcome.Active);

        // No parent-edge written by the bind logic — the install
        // pipeline only acts when `--into` is a real unit reference.
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var edges = await db.UnitSubunitMemberships.IgnoreQueryFilters().ToListAsync(TestContext.Current.CancellationToken);
        edges.Any(e => e.ParentId != tenantId && e.ParentId != Guid.Empty).ShouldBeFalse(
            "--into tenant must not write a unit-parent edge");
    }

    [Fact]
    public async Task InstallAsync_IntoUnit_Guid_BindsTopLevelToParent()
    {
        var (service, scopeFactory, tenantId) = BuildService();

        // Seed an existing parent unit in the DB so the IntoUnit
        // resolution succeeds.
        var parentId = Guid.NewGuid();
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
            db.UnitDefinitions.Add(new UnitDefinitionEntity
            {
                Id = parentId,
                TenantId = tenantId,
                DisplayName = "existing-parent",
                Description = string.Empty,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var pkg = BuildPackageWithOneUnit(name: "pkg-into-guid");
        await service.InstallAsync(
            new[] { new InstallTarget(
                PackageName: "pkg-into-guid",
                Inputs: new Dictionary<string, string>(),
                OriginalYaml: pkg.PackageYaml,
                PackageRoot: pkg.PackageRoot,
                IntoUnit: parentId.ToString()) },
            TestContext.Current.CancellationToken);

        await using var scope2 = scopeFactory.CreateAsyncScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<SpringDbContext>();
        var edges = await db2.UnitSubunitMemberships.IgnoreQueryFilters().ToListAsync(TestContext.Current.CancellationToken);
        // The capturing activator does NOT write a tenant-edge for us
        // (it's a no-op); the install service should still write the
        // parent-edge via BindTopLevelArtefactsToParentAsync.
        edges.Any(e => e.ParentId == parentId).ShouldBeTrue(
            "the install service should bind the top-level unit to the chosen parent");
    }

    [Fact]
    public async Task InstallAsync_IntoUnit_DisplayName_BindsTopLevelToParent()
    {
        var (service, scopeFactory, tenantId) = BuildService();
        var parentId = Guid.NewGuid();
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
            db.UnitDefinitions.Add(new UnitDefinitionEntity
            {
                Id = parentId,
                TenantId = tenantId,
                DisplayName = "engineering",
                Description = string.Empty,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var pkg = BuildPackageWithOneUnit(name: "pkg-into-name");
        await service.InstallAsync(
            new[] { new InstallTarget(
                PackageName: "pkg-into-name",
                Inputs: new Dictionary<string, string>(),
                OriginalYaml: pkg.PackageYaml,
                PackageRoot: pkg.PackageRoot,
                IntoUnit: "engineering") },
            TestContext.Current.CancellationToken);

        await using var scope2 = scopeFactory.CreateAsyncScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<SpringDbContext>();
        var edges = await db2.UnitSubunitMemberships.IgnoreQueryFilters().ToListAsync(TestContext.Current.CancellationToken);
        edges.Any(e => e.ParentId == parentId).ShouldBeTrue();
    }

    [Fact]
    public async Task InstallAsync_IntoUnit_TopLevelAgent_WritesUnitMembership()
    {
        var (service, scopeFactory, tenantId) = BuildService();
        var parentId = Guid.NewGuid();
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
            db.UnitDefinitions.Add(new UnitDefinitionEntity
            {
                Id = parentId,
                TenantId = tenantId,
                DisplayName = "team-host",
                Description = string.Empty,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Package with one top-level agent.
        using var pkg = BuildPackageWithOneAgent(name: "pkg-into-agent");
        await service.InstallAsync(
            new[] { new InstallTarget(
                PackageName: "pkg-into-agent",
                Inputs: new Dictionary<string, string>(),
                OriginalYaml: pkg.PackageYaml,
                PackageRoot: pkg.PackageRoot,
                IntoUnit: "team-host") },
            TestContext.Current.CancellationToken);

        await using var scope2 = scopeFactory.CreateAsyncScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<SpringDbContext>();
        var memberships = await db2.UnitMemberships.IgnoreQueryFilters().ToListAsync(TestContext.Current.CancellationToken);
        memberships.Any(m => m.UnitId == parentId).ShouldBeTrue(
            "the install service should add a unit_memberships row for top-level agents under --into");
    }

    [Fact]
    public async Task InstallAsync_IntoUnit_Unknown_Rejects400()
    {
        var (service, _, _) = BuildService();
        using var pkg = BuildPackageWithOneUnit(name: "pkg-into-unknown");
        await Should.ThrowAsync<InvalidInstallScopeException>(async () =>
            await service.InstallAsync(
                new[] { new InstallTarget(
                    PackageName: "pkg-into-unknown",
                    Inputs: new Dictionary<string, string>(),
                    OriginalYaml: pkg.PackageYaml,
                    PackageRoot: pkg.PackageRoot,
                    IntoUnit: "does-not-exist") },
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InstallAsync_IntoUnit_PackageName_Rejected()
    {
        var (service, _, _) = BuildService();
        using var pkg = BuildPackageWithOneUnit(name: "pkg-into-self");
        var ex = await Should.ThrowAsync<InvalidInstallScopeException>(async () =>
            await service.InstallAsync(
                new[] { new InstallTarget(
                    PackageName: "pkg-into-self",
                    Inputs: new Dictionary<string, string>(),
                    OriginalYaml: pkg.PackageYaml,
                    PackageRoot: pkg.PackageRoot,
                    IntoUnit: "pkg-into-self") },  // matches package name
                TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("package name");
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private sealed class NoOpActivator : IPackageArtefactActivator
    {
        public Task ActivateAsync(
            string packageName,
            ResolvedArtefact artefact,
            Guid installId,
            LocalSymbolMap symbolMap,
            IReadOnlyDictionary<string, ConnectorBinding>? connectorBindings = null,
            ResolvedExecutionDefaults? executionDefaults = null,
            string? displayNameOverride = null,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private static (PackageInstallService Service, IServiceScopeFactory ScopeFactory, Guid TenantId) BuildService()
    {
        var dbName = $"sv-into-{Guid.NewGuid():N}";
        var services = new ServiceCollection();
        services.AddDbContext<SpringDbContext>(opt => opt
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w =>
                w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning)));

        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
        var directory = Substitute.For<IDirectoryService>();

        var credentialResolver = Substitute.For<ILlmCredentialResolver>();
        credentialResolver
            .ResolveAsync(Arg.Any<string>(), Arg.Any<AuthMethod>(), Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(new LlmCredentialResolution(null, LlmCredentialSource.NotFound, string.Empty)));

        var tenantId = OssTenantIds.Default;
        var tenantContext = Substitute.For<ITenantContext>();
        tenantContext.CurrentTenantId.Returns(tenantId);

        var service = new PackageInstallService(
            scopeFactory,
            directory,
            new NoOpActivator(),
            Cvoya.Spring.RuntimeCatalog.RuntimeCatalogLoader.LoadEmbedded(),
            credentialResolver,
            Substitute.For<ISecretStore>(),
            Substitute.For<ISecretRegistry>(),
            tenantContext,
            NullLogger<PackageInstallService>.Instance);

        return (service, scopeFactory, tenantId);
    }

    private static TempPackage BuildPackageWithOneUnit(string name)
    {
        var root = Path.Combine(Path.GetTempPath(), "sv-into-pkg-" + Path.GetRandomFileName());
        Directory.CreateDirectory(root);
        var packageYaml = $"""
            apiVersion: spring.voyage/v1
            kind: Package
            name: {name}
            description: x
            version: 1.0.0
            execution:
              image: ghcr.io/example/agents:latest
            """;
        File.WriteAllText(Path.Combine(root, "package.yaml"), packageYaml);
        var unitDir = Path.Combine(root, "units", "alpha");
        Directory.CreateDirectory(unitDir);
        File.WriteAllText(Path.Combine(unitDir, "package.yaml"), """
            apiVersion: spring.voyage/v1
            kind: Unit
            name: alpha
            description: x
            """);
        return new TempPackage(root, packageYaml);
    }

    private static TempPackage BuildPackageWithOneAgent(string name)
    {
        var root = Path.Combine(Path.GetTempPath(), "sv-into-pkg-" + Path.GetRandomFileName());
        Directory.CreateDirectory(root);
        var packageYaml = $"""
            apiVersion: spring.voyage/v1
            kind: Package
            name: {name}
            description: x
            version: 1.0.0
            """;
        File.WriteAllText(Path.Combine(root, "package.yaml"), packageYaml);
        var agentDir = Path.Combine(root, "agents", "worker");
        Directory.CreateDirectory(agentDir);
        File.WriteAllText(Path.Combine(agentDir, "package.yaml"), """
            apiVersion: spring.voyage/v1
            kind: Agent
            name: worker
            description: x
            """);
        return new TempPackage(root, packageYaml);
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
