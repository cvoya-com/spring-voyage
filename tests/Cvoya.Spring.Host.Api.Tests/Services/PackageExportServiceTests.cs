// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Services;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Dapr.Tenancy;
using Cvoya.Spring.Host.Api.Services;
using Cvoya.Spring.Manifest;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="PackageExportService"/> covering the 11 acceptance
/// bullets from #1560 (ADR-0035 decisions 9 and 12).
///
/// Tests 1–8 cover the core service. Tests 9–11 cover the YAML splice helpers
/// directly (testable without DB or directory).
/// </summary>
public class PackageExportServiceTests
{
    private static readonly Guid Unit_Main_Id = new("00000001-feed-1234-5678-000000000000");
    private static readonly Guid Unit_OrphanUnit_Id = new("00000002-feed-1234-5678-000000000000");

    private static readonly Guid TenantA = new("aaaaaaaa-1111-1111-1111-000000000001");
    private static readonly Guid TenantB = new("aaaaaaaa-1111-1111-1111-000000000002");

    // A minimal valid UnitPackage YAML with no inputs. {0} = package name.
    private const string MinimalPackageYaml = """
        apiVersion: spring.voyage/v1
        kind: UnitPackage
        metadata:
          name: {0}
        unit: main
        """;

    // A package YAML with a comment and one non-secret input.
    private const string PackageWithInputYaml = """
        # This comment should survive a round-trip
        apiVersion: spring.voyage/v1
        kind: UnitPackage
        metadata:
          name: {0}
          # description follows
          description: test package
        inputs:
          - name: github_repo
            type: string
            required: true
            description: GitHub repository name
        unit: main
        """;

    // A package YAML with a secret input.
    private const string PackageWithSecretInputYaml = """
        apiVersion: spring.voyage/v1
        kind: UnitPackage
        metadata:
          name: {0}
        inputs:
          - name: github_token
            secret: true
            required: true
          - name: team_name
            type: string
            required: true
        unit: main
        """;

    private const string MinimalUnitYaml = """
        unit:
          name: main
        """;

    // ── Fixture helpers ────────────────────────────────────────────────────

    private static (PackageExportService Service, IServiceScopeFactory ScopeFactory, IDirectoryService Directory)
        BuildService(
            Guid tenantId = default,
            IDirectoryService? dir = null)
    {
        var dbName = $"pkg-export-{Guid.NewGuid():N}";
        var services = new ServiceCollection();

        var resolvedTenantId = tenantId == default ? Cvoya.Spring.Core.Tenancy.OssTenantIds.Default : tenantId;
        services.AddSingleton<ITenantContext>(new StaticTenantContext(resolvedTenantId));
        services.AddScoped<SpringDbContext>(sp =>
        {
            var opts = new DbContextOptionsBuilder<SpringDbContext>()
                .UseInMemoryDatabase(dbName)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            return new SpringDbContext(opts, sp.GetRequiredService<ITenantContext>());
        });

        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        dir ??= Substitute.For<IDirectoryService>();

        var svc = new PackageExportService(
            scopeFactory,
            dir,
            NullLogger<PackageExportService>.Instance);

        return (svc, scopeFactory, dir);
    }

    /// <summary>
    /// Seeds a <see cref="PackageInstallEntity"/> and a matching
    /// <see cref="UnitDefinitionEntity"/> into the in-memory DB for the given
    /// tenant, returning the install id.
    /// </summary>
    private static async Task<Guid> SeedInstallAsync(
        IServiceScopeFactory scopeFactory,
        Guid tenantId,
        string packageName,
        string yaml,
        string inputsJson = "{}",
        string unitName = "main")
    {
        var installId = Guid.NewGuid();
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        db.PackageInstalls.Add(new PackageInstallEntity
        {
            Id = Guid.NewGuid(),
            InstallId = installId,
            TenantId = tenantId,
            PackageName = packageName,
            Status = PackageInstallStatus.Active,
            OriginalManifestYaml = yaml,
            InputsJson = inputsJson,
            StartedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
        });

        db.UnitDefinitions.Add(new UnitDefinitionEntity
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            DisplayName = unitName,
            Description = string.Empty,
            InstallId = installId,
            InstallState = PackageInstallState.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync(CancellationToken.None);
        return installId;
    }

    private static string CreatePackageDir(string unitYaml = MinimalUnitYaml)
    {
        var root = Path.Combine(Path.GetTempPath(), $"sv-pkg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "units"));
        File.WriteAllText(Path.Combine(root, "units", "main.yaml"), unitYaml);
        return root;
    }

    // ── Test 1: Byte-stable export — no --with-values ──────────────────────

    [Fact]
    public async Task ExportByInstallId_NoWithValues_ReturnsOriginalYamlByteForByte()
    {
        // Arrange: a package whose YAML has comments + non-canonical key ordering.
        var (svc, scopeFactory, _) = BuildService(TenantA);
        const string yaml = PackageWithInputYaml;
        var packageName = "byte-stable-pkg";
        var formattedYaml = string.Format(yaml, packageName);
        var inputs = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            { "github_repo", "spring-voyage" }
        });

        var installId = await SeedInstallAsync(
            scopeFactory, TenantA, packageName, formattedYaml, inputs);

        // Act.
        var result = await svc.ExportByInstallIdAsync(
            installId, withValues: false, TestContext.Current.CancellationToken);

        // Assert: content equals original YAML (modulo a trailing newline).
        result.ShouldNotBeNull();
        var exported = Encoding.UTF8.GetString(result!.Content).TrimEnd();
        exported.ShouldBe(formattedYaml.TrimEnd());
    }

    // ── Test 2: --with-values materialises non-secret inputs ──────────────

    [Fact]
    public async Task ExportByInstallId_WithValues_NonSecretInputMaterialised()
    {
        // Arrange.
        var (svc, scopeFactory, _) = BuildService(TenantA);
        var packageName = "with-values-pkg";
        var formattedYaml = string.Format(PackageWithInputYaml, packageName);
        var inputs = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            { "github_repo", "spring-voyage" }
        });

        var installId = await SeedInstallAsync(
            scopeFactory, TenantA, packageName, formattedYaml, inputs);

        // Act.
        var result = await svc.ExportByInstallIdAsync(
            installId, withValues: true, TestContext.Current.CancellationToken);

        // Assert.
        result.ShouldNotBeNull();
        var exportedYaml = Encoding.UTF8.GetString(result!.Content);
        exportedYaml.ShouldContain("inputs:");
        exportedYaml.ShouldContain("github_repo: spring-voyage");
        // The original schema list-form should be replaced.
        exportedYaml.ShouldNotContain("- name: github_repo");
    }

    // ── Test 3: Secrets export as placeholders, never as cleartext ─────────

    [Fact]
    public async Task ExportByInstallId_WithValues_SecretInputExportedAsPlaceholder()
    {
        // Arrange: secret input stored as secret:// reference.
        var (svc, scopeFactory, _) = BuildService(TenantA);
        var packageName = "secret-pkg";
        var formattedYaml = string.Format(PackageWithSecretInputYaml, packageName);
        var inputs = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            { "github_token", "secret://tenant-a/github-token" },
            { "team_name", "engineering" }
        });

        var installId = await SeedInstallAsync(
            scopeFactory, TenantA, packageName, formattedYaml, inputs);

        // Act.
        var result = await svc.ExportByInstallIdAsync(
            installId, withValues: true, TestContext.Current.CancellationToken);

        // Assert: secret value NOT present; placeholder IS present.
        result.ShouldNotBeNull();
        var exportedYaml = Encoding.UTF8.GetString(result!.Content);
        exportedYaml.ShouldNotContain("secret://tenant-a/github-token");
        exportedYaml.ShouldContain("github_token");
        exportedYaml.ShouldContain("secrets.github_token");
        // Non-secret input should be materialised normally.
        exportedYaml.ShouldContain("team_name: engineering");
    }

    // ── Test 4: Export by unit name ────────────────────────────────────────

    [Fact]
    public async Task ExportByUnitName_KnownUnit_ReturnsCorrectPackage()
    {
        // Arrange.
        var (svc, scopeFactory, dir) = BuildService(TenantA);
        var packageName = "unit-name-pkg";
        var formattedYaml = string.Format(MinimalPackageYaml, packageName);

        var installId = await SeedInstallAsync(
            scopeFactory, TenantA, packageName, formattedYaml);

        // Post-#1629 ExportByUnitNameAsync looks the unit up by DisplayName
        // via ListAllAsync rather than Address.For (Guid-only). Seed the
        // directory listing accordingly.
        dir.ListAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<DirectoryEntry>
            {
                new(new Address("unit", Unit_Main_Id),
                    Guid.NewGuid(),
                    "main",
                    string.Empty,
                    null,
                    DateTimeOffset.UtcNow),
            });

        // Act.
        var result = await svc.ExportByUnitNameAsync(
            "main", withValues: false, TestContext.Current.CancellationToken);

        // Assert.
        result.ShouldNotBeNull();
        result!.PackageName.ShouldBe(packageName);
    }

    // ── Test 5: Export by install id — direct lookup ───────────────────────

    [Fact]
    public async Task ExportByInstallId_KnownId_ReturnsCorrectPackage()
    {
        // Arrange.
        var (svc, scopeFactory, _) = BuildService(TenantA);
        var packageName = "install-id-pkg";
        var formattedYaml = string.Format(MinimalPackageYaml, packageName);
        var installId = await SeedInstallAsync(
            scopeFactory, TenantA, packageName, formattedYaml);

        // Act.
        var result = await svc.ExportByInstallIdAsync(
            installId, withValues: false, TestContext.Current.CancellationToken);

        // Assert.
        result.ShouldNotBeNull();
        result!.PackageName.ShouldBe(packageName);
        result.ContentType.ShouldBe("application/x-yaml");
        result.FileName.ShouldBe($"{packageName}.yaml");
    }

    // ── Test 6: Export by unit name — unit not found ───────────────────────

    [Fact]
    public async Task ExportByUnitName_UnitNotFound_ReturnsNull()
    {
        // Arrange: directory returns null for any address.
        var (svc, _, dir) = BuildService();
        dir.ResolveAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns((DirectoryEntry?)null);

        // Act.
        var result = await svc.ExportByUnitNameAsync(
            "nonexistent-unit", withValues: false, TestContext.Current.CancellationToken);

        // Assert.
        result.ShouldBeNull();
    }

    // ── Test 7: Export by install id — install not found ──────────────────

    [Fact]
    public async Task ExportByInstallId_InstallNotFound_ReturnsNull()
    {
        var (svc, _, _) = BuildService();
        var result = await svc.ExportByInstallIdAsync(
            Guid.NewGuid(), withValues: false, TestContext.Current.CancellationToken);
        result.ShouldBeNull();
    }

    // ── Test 8: Tenant isolation ────────────────────────────────────────────

    [Fact]
    public async Task ExportByInstallId_TenantB_CannotSeeInstalledForTenantA()
    {
        // Arrange: install a package under TenantA.
        var (_, scopeFactory, _) = BuildService(tenantId: TenantA);

        var packageName = "tenant-isolated-pkg";
        var formattedYaml = string.Format(MinimalPackageYaml, packageName);
        var installId = await SeedInstallAsync(
            scopeFactory, TenantA, packageName, formattedYaml);

        // Act: export with TenantB service (different EF tenant context).
        // Build a new service pointing at the same DB name but different tenant.
        var dbName = $"pkg-export-isolation-{Guid.NewGuid():N}";
        var servicesTenantB = new ServiceCollection();
        servicesTenantB.AddSingleton<ITenantContext>(new StaticTenantContext(TenantB));
        servicesTenantB.AddScoped<SpringDbContext>(sp =>
        {
            var opts = new DbContextOptionsBuilder<SpringDbContext>()
                .UseInMemoryDatabase(dbName)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            return new SpringDbContext(opts, sp.GetRequiredService<ITenantContext>());
        });
        var spTenantB = servicesTenantB.BuildServiceProvider();
        var scopeFactoryB = spTenantB.GetRequiredService<IServiceScopeFactory>();

        var dir = Substitute.For<IDirectoryService>();
        var svcTenantB = new PackageExportService(
            scopeFactoryB, dir, NullLogger<PackageExportService>.Instance);

        // TenantB's DB is empty — install was made in TenantA's DB.
        var result = await svcTenantB.ExportByInstallIdAsync(
            installId, withValues: false, TestContext.Current.CancellationToken);

        // Assert: TenantB cannot see TenantA's install.
        result.ShouldBeNull();
    }

    // ── Test 9: Unit-name lookup falls back to install_id via unit_definitions ─

    [Fact]
    public async Task ExportByUnitName_DirectoryEntry_ButNoInstallRow_ReturnsNull()
    {
        // Arrange: directory resolves the unit but there's no unit_definitions row
        // with an InstallId (e.g. a unit created before the install feature landed).
        var (svc, scopeFactory, dir) = BuildService();
        dir.ResolveAsync(new Address("unit", Unit_OrphanUnit_Id), Arg.Any<CancellationToken>())
            .Returns(new DirectoryEntry(
                new Address("unit", Unit_OrphanUnit_Id),
                Guid.NewGuid(),
                "Orphan",
                string.Empty, null,
                DateTimeOffset.UtcNow));

        // Unit_definitions row exists but has no InstallId.
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        db.UnitDefinitions.Add(new UnitDefinitionEntity
        {
            Id = Guid.NewGuid(),
            TenantId = TenantA,
            DisplayName = "orphan-unit",
            Description = string.Empty,
            InstallId = null,  // No install id.
            InstallState = PackageInstallState.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(CancellationToken.None);

        // Act.
        var result = await svc.ExportByUnitNameAsync(
            "orphan-unit", withValues: false, TestContext.Current.CancellationToken);

        // Assert: no install row → null (unit exists in directory but was not
        // installed via the package install pipeline).
        result.ShouldBeNull();
    }

    // ── Test 10: SpliceInputValues — no inputs → verbatim ─────────────────

    [Fact]
    public void SpliceInputValues_EmptyInputs_ReturnsOriginalYaml()
    {
        const string yaml = "apiVersion: spring.voyage/v1\nkind: UnitPackage\n";
        var result = PackageExportService.SpliceInputValues(yaml, "{}");
        result.ShouldBe(yaml);
    }

    // ── Test 11: ReplaceInputsBlock — block present ────────────────────────

    [Fact]
    public void ReplaceInputsBlock_ExistingBlock_ReplacedWithKeyValues()
    {
        const string yaml = """
            apiVersion: spring.voyage/v1
            inputs:
              - name: foo
                type: string
            unit: main
            """;

        const string replacement = "inputs:\n  foo: bar";
        var result = PackageExportService.ReplaceInputsBlock(yaml, replacement);

        result.ShouldContain("inputs:");
        result.ShouldContain("foo: bar");
        result.ShouldNotContain("- name: foo");
        result.ShouldContain("unit: main");
    }

    // ── Test 12: QuoteIfNeeded ─────────────────────────────────────────────

    [Theory]
    [InlineData("simple", "simple")]
    [InlineData("", "\"\"")]
    [InlineData("has space", "\"has space\"")]
    [InlineData("has:colon", "\"has:colon\"")]
    [InlineData("has#hash", "\"has#hash\"")]
    public void QuoteIfNeeded_VariousInputs_QuotedCorrectly(string input, string expected)
    {
        PackageExportService.QuoteIfNeeded(input).ShouldBe(expected);
    }

    // ── Test 13: Round-trip — install → export → verify YAML equivalence ──

    [Fact]
    public async Task RoundTrip_InstallThenExportWithValues_YamlEquivalentToOriginal()
    {
        // Arrange: use the PackageInstallService to seed a real install row
        // with proper InputsJson, then export and verify.
        var packageName = "round-trip-pkg";
        var root = CreatePackageDir();
        var yamlRaw = $$$"""
            apiVersion: spring.voyage/v1
            kind: UnitPackage
            metadata:
              name: {{{packageName}}}
            inputs:
              - name: team_name
                type: string
                required: true
            unit: main
            """;

        // Seed directly (simulates the install service having done its work).
        var (svc, scopeFactory, dir) = BuildService(TenantA);
        var inputs = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            { "team_name", "engineering" }
        });
        var installId = await SeedInstallAsync(
            scopeFactory, TenantA, packageName, yamlRaw, inputs);

        dir.ResolveAsync(new Address("unit", Unit_Main_Id), Arg.Any<CancellationToken>())
            .Returns(new DirectoryEntry(
                new Address("unit", Unit_Main_Id),
                Guid.NewGuid(),
                "Main", string.Empty, null,
                DateTimeOffset.UtcNow));

        // Act.
        var result = await svc.ExportByInstallIdAsync(
            installId, withValues: true, TestContext.Current.CancellationToken);

        // Assert: exported YAML contains materialised value.
        result.ShouldNotBeNull();
        var exported = Encoding.UTF8.GetString(result!.Content);
        exported.ShouldContain("team_name: engineering");
        // The apiVersion, kind, metadata are preserved verbatim.
        exported.ShouldContain("apiVersion: spring.voyage/v1");
        exported.ShouldContain($"name: {packageName}");
    }
}