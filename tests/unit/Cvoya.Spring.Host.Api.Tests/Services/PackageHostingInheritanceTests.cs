// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Services;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Core.Artefacts;
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
/// Inheritance tests for issue #2436 — when an Agent artefact has no
/// <c>execution.hosting</c> declaration of its own (and no template
/// already merged one in via ADR-0043 §5d), the install pipeline must
/// project the parent unit's <c>execution.hosting</c> literal onto the
/// agent at activation time. Precedence: agent &gt; template &gt; unit
/// &gt; default (<c>persistent</c>).
/// </summary>
public class PackageHostingInheritanceTests
{
    [Fact]
    public async Task UnitHosting_InheritedByMemberAgentLackingOwn()
    {
        // Unit declares execution.hosting: ephemeral. The member agent
        // (nested under units/parent/agents/member/) declares no hosting
        // of its own, so the install pipeline should resolve "ephemeral"
        // as the inherited value and pass it to the activator.
        using var pkg = await BuildPackageAsync(
            packageYaml: """
                apiVersion: spring.voyage/v1
                kind: Package
                name: pkg-hosting-inherit
                description: x
                version: 1.0.0
                """,
            artefacts: new[]
            {
                ("units/parent/package.yaml", """
                    apiVersion: spring.voyage/v1
                    kind: Unit
                    name: parent
                    description: x
                    execution:
                      image: ghcr.io/example/agents:latest
                      hosting: ephemeral
                    """),
                ("units/parent/agents/member/package.yaml", """
                    apiVersion: spring.voyage/v1
                    kind: Agent
                    name: member
                    description: x
                    """),
            });

        var (service, activator) = BuildService();

        await service.InstallAsync(
            new[] { new InstallTarget(
                PackageName: "pkg-hosting-inherit",
                Inputs: new Dictionary<string, string>(),
                OriginalYaml: pkg.PackageYaml,
                PackageRoot: pkg.PackageRoot) },
            TestContext.Current.CancellationToken);

        activator.CapturedAgentHosting.ShouldContainKey("member");
        activator.CapturedAgentHosting["member"].ShouldBe("ephemeral");
    }

    [Fact]
    public async Task AgentExplicitHosting_StaysOnAgent_PipelineStillCarriesParentValue()
    {
        // Agent declares execution.hosting: pooled. The pipeline still
        // resolves the parent unit's hosting and passes it as the
        // inherited value — but the activator's own precedence rule keeps
        // the agent's own value when both are present. This test asserts
        // the install pipeline does NOT skip the inheritance computation
        // when the agent has its own value; the agent's YAML still wins
        // downstream because the activator checks `projected["hosting"]`
        // before applying the inherited value.
        using var pkg = await BuildPackageAsync(
            packageYaml: """
                apiVersion: spring.voyage/v1
                kind: Package
                name: pkg-hosting-explicit
                description: x
                version: 1.0.0
                """,
            artefacts: new[]
            {
                ("units/parent/package.yaml", """
                    apiVersion: spring.voyage/v1
                    kind: Unit
                    name: parent
                    description: x
                    execution:
                      image: ghcr.io/example/agents:latest
                      hosting: persistent
                    """),
                ("units/parent/agents/member/package.yaml", """
                    apiVersion: spring.voyage/v1
                    kind: Agent
                    name: member
                    description: x
                    execution:
                      hosting: pooled
                    """),
            });

        var (service, activator) = BuildService();

        await service.InstallAsync(
            new[] { new InstallTarget(
                PackageName: "pkg-hosting-explicit",
                Inputs: new Dictionary<string, string>(),
                OriginalYaml: pkg.PackageYaml,
                PackageRoot: pkg.PackageRoot) },
            TestContext.Current.CancellationToken);

        activator.CapturedAgentHosting.ShouldContainKey("member");
        activator.CapturedAgentHosting["member"].ShouldBe("persistent");
    }

    [Fact]
    public async Task NoHostingAnywhere_AgentCarriesNullInheritedValue()
    {
        // Neither unit nor agent declares hosting. The install pipeline
        // passes null as the inherited value; the activator falls
        // through to the dispatcher's hard default of `persistent` at
        // runtime. The unit still declares `execution.image:` so the
        // ExecutionDefaultsResolver pre-flight passes (image is
        // hard-required on every unit at activation time).
        using var pkg = await BuildPackageAsync(
            packageYaml: """
                apiVersion: spring.voyage/v1
                kind: Package
                name: pkg-hosting-absent
                description: x
                version: 1.0.0
                """,
            artefacts: new[]
            {
                ("units/parent/package.yaml", """
                    apiVersion: spring.voyage/v1
                    kind: Unit
                    name: parent
                    description: x
                    execution:
                      image: ghcr.io/example/agents:latest
                    """),
                ("units/parent/agents/member/package.yaml", """
                    apiVersion: spring.voyage/v1
                    kind: Agent
                    name: member
                    description: x
                    """),
            });

        var (service, activator) = BuildService();

        await service.InstallAsync(
            new[] { new InstallTarget(
                PackageName: "pkg-hosting-absent",
                Inputs: new Dictionary<string, string>(),
                OriginalYaml: pkg.PackageYaml,
                PackageRoot: pkg.PackageRoot) },
            TestContext.Current.CancellationToken);

        activator.CapturedAgentHosting.ShouldContainKey("member");
        activator.CapturedAgentHosting["member"].ShouldBeNull();
    }

    [Fact]
    public async Task TopLevelAgent_NoParentUnit_NullInheritance()
    {
        // Top-level agent (no containing unit). The pipeline has no
        // parent unit hosting to inherit from, so the inherited value is
        // null and the activator leaves the agent's execution.hosting
        // alone (falling through to the dispatcher's `persistent`
        // default at runtime).
        using var pkg = await BuildPackageAsync(
            packageYaml: """
                apiVersion: spring.voyage/v1
                kind: Package
                name: pkg-hosting-top
                description: x
                version: 1.0.0
                """,
            artefacts: new[]
            {
                ("agents/topagent/package.yaml", """
                    apiVersion: spring.voyage/v1
                    kind: Agent
                    name: topagent
                    description: x
                    """),
            });

        var (service, activator) = BuildService();

        await service.InstallAsync(
            new[] { new InstallTarget(
                PackageName: "pkg-hosting-top",
                Inputs: new Dictionary<string, string>(),
                OriginalYaml: pkg.PackageYaml,
                PackageRoot: pkg.PackageRoot) },
            TestContext.Current.CancellationToken);

        activator.CapturedAgentHosting.ShouldContainKey("topagent");
        activator.CapturedAgentHosting["topagent"].ShouldBeNull();
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Recording <see cref="IPackageArtefactActivator"/>: captures the
    /// <c>inheritedAgentHosting</c> argument the install service passes
    /// per agent, plus the per-unit execution defaults (for shared
    /// fixture compatibility with the preflight tests).
    /// </summary>
    private sealed class CapturingActivator : IPackageArtefactActivator
    {
        public Dictionary<string, string?> CapturedAgentHosting { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public Task ActivateAsync(
            string packageName,
            ResolvedArtefact artefact,
            Guid installId,
            LocalSymbolMap symbolMap,
            IReadOnlyDictionary<string, ConnectorBinding>? connectorBindings = null,
            ResolvedExecutionDefaults? executionDefaults = null,
            string? displayNameOverride = null,
            string? inheritedAgentHosting = null,
            IReadOnlyDictionary<string, Guid>? humanOverrides = null,
            CancellationToken cancellationToken = default)
        {
            if (artefact.Kind == ArtefactKind.Agent)
            {
                CapturedAgentHosting[artefact.Name] = inheritedAgentHosting;
            }
            return Task.CompletedTask;
        }
    }

    private static (PackageInstallService Service, CapturingActivator Activator) BuildService()
    {
        var dbName = $"sv-2436-{Guid.NewGuid():N}";
        var services = new ServiceCollection();
        services.AddDbContext<SpringDbContext>(opt => opt
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w =>
                w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning)));

        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
        var directory = Substitute.For<IDirectoryService>();
        var activator = new CapturingActivator();

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
    /// Builds a package tree from a list of (relative-path, content)
    /// tuples. Mirrors the helper in
    /// <c>PackageInstallServiceExecutionPreflightTests</c> but accepts
    /// arbitrary nested paths so the test can place agents under
    /// <c>units/&lt;u&gt;/agents/&lt;a&gt;/</c>.
    /// </summary>
    private static async Task<TempPackage> BuildPackageAsync(
        string packageYaml,
        (string RelativePath, string Content)[] artefacts)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "sv-2436-" + Path.GetRandomFileName());
        Directory.CreateDirectory(tempRoot);
        var packagePath = Path.Combine(tempRoot, "package.yaml");
        await File.WriteAllTextAsync(packagePath, packageYaml);
        foreach (var (relativePath, content) in artefacts)
        {
            var full = Path.Combine(tempRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllTextAsync(full, content);
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
