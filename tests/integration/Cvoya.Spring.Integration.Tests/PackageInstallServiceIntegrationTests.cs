// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Integration.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.DependencyInjection;
using Cvoya.Spring.Host.Api.Services;
using Cvoya.Spring.Manifest;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// End-to-end coverage for <see cref="PackageInstallService"/> against the
/// real in-repo packages (#2311). The tests stand up the Dapr DI graph
/// with an in-memory <see cref="SpringDbContext"/>, point the catalog at
/// <c>packages/</c> in the repo root, and substitute
/// <see cref="IActorProxyFactory"/> with a fake that delegates
/// <see cref="IUnitActor.AddMemberAsync"/> calls to a real
/// <see cref="IUnitMembershipCoordinator"/> — so the activation pipeline
/// writes <c>unit_memberships</c> rows through the same path the runtime
/// uses, without standing up a live Dapr placement.
/// </summary>
/// <remarks>
/// Pattern mirrors <see cref="DefaultTenantBootstrapTests"/>: in-memory
/// EF, DI-graph composition, no Testcontainers. The tests are
/// deliberately conservative — they verify the directory rows + the
/// Guid-shape of the agent address (the regression #2309 fixed) and the
/// membership wiring. Actor-runtime side-effects (dispatcher launch,
/// lifecycle transitions) are out of scope for the install pipeline.
/// </remarks>
public class PackageInstallServiceIntegrationTests : IDisposable
{
    private readonly string _packagesRoot;
    private readonly List<ServiceProvider> _providers = new();

    public PackageInstallServiceIntegrationTests()
    {
        _packagesRoot = LocateRepoPackagesRoot();
    }

    public void Dispose()
    {
        foreach (var p in _providers)
        {
            try { p.Dispose(); } catch { /* best effort */ }
        }
    }

    // ── (1) hello-world ──────────────────────────────────────────────────

    [Fact]
    public async Task InstallHelloWorld_HappyPath()
    {
        using var fx = BuildFixture();

        var result = await fx.Service.InstallAsync(
            new[] { LoadTarget("hello-world") },
            TestContext.Current.CancellationToken);

        result.PackageResults.Single().Status.ShouldBe(PackageInstallOutcome.Active);

        await using var scope = fx.ScopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        // hello-world Unit row
        var unitRow = await db.UnitDefinitions
            .IgnoreQueryFilters()
            .Where(u => u.DisplayName == "hello-world")
            .FirstOrDefaultAsync(TestContext.Current.CancellationToken);
        unitRow.ShouldNotBeNull("hello-world unit row should be written by the install");
        unitRow!.Id.ShouldNotBe(Guid.Empty);

        // greeter Agent row
        var agentRow = await db.AgentDefinitions
            .IgnoreQueryFilters()
            .Where(a => a.DisplayName == "greeter")
            .FirstOrDefaultAsync(TestContext.Current.CancellationToken);
        agentRow.ShouldNotBeNull("greeter agent row should be written by the install");
        agentRow!.Id.ShouldNotBe(Guid.Empty);

        // Agent's directory address must be Guid-formatted, not slug-formatted
        // (the regression #2309 fixed — Address.For(slug) would explode on a
        // non-Guid path).
        var agentAddress = Address.ForIdentity(Address.AgentScheme, agentRow.Id);
        agentAddress.Path.ShouldBe(GuidFormatter.Format(agentRow.Id));
        agentAddress.Path.ShouldNotContain("-");

        // unit_memberships row links the agent to the unit.
        var membership = await db.UnitMemberships
            .IgnoreQueryFilters()
            .Where(m => m.UnitId == unitRow.Id && m.AgentId == agentRow.Id)
            .FirstOrDefaultAsync(TestContext.Current.CancellationToken);
        membership.ShouldNotBeNull(
            "unit_memberships row should link the greeter agent to the hello-world unit");
    }

    // ── (2) example-simple ───────────────────────────────────────────────

    [Fact]
    public async Task InstallExampleSimple_HappyPath()
    {
        using var fx = BuildFixture();

        var result = await fx.Service.InstallAsync(
            new[] { LoadTarget("example-simple") },
            TestContext.Current.CancellationToken);

        result.PackageResults.Single().Status.ShouldBe(PackageInstallOutcome.Active);

        await using var scope = fx.ScopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        // 1 unit
        var unitRows = await db.UnitDefinitions
            .IgnoreQueryFilters()
            .Where(u => u.DisplayName == "greeting-team")
            .ToListAsync(TestContext.Current.CancellationToken);
        unitRows.Count.ShouldBe(1);
        var unitId = unitRows[0].Id;

        // 2 agents
        var friendly = await db.AgentDefinitions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(a => a.DisplayName == "friendly-greeter",
                TestContext.Current.CancellationToken);
        friendly.ShouldNotBeNull();
        var polite = await db.AgentDefinitions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(a => a.DisplayName == "polite-greeter",
                TestContext.Current.CancellationToken);
        polite.ShouldNotBeNull();
        friendly!.Id.ShouldNotBe(polite!.Id);

        // Each agent has a unit_memberships row pointing at the greeting-team
        // unit (the regression #2309 guard — pre-fix slug-resolution would
        // have minted a different unit-id and stranded the agents).
        var memberships = await db.UnitMemberships
            .IgnoreQueryFilters()
            .Where(m => m.UnitId == unitId)
            .ToListAsync(TestContext.Current.CancellationToken);
        memberships.Count.ShouldBe(2,
            "the unit should resolve both members to its own Guid, not to phantom ids");
        memberships.Select(m => m.AgentId).ShouldBe(
            new[] { friendly.Id, polite.Id }, ignoreOrder: true);
    }

    // ── (2b) #2388: agent Definition carries the canonical execution block ─

    /// <summary>
    /// Regression guard for #2388 bug 2. The install pipeline now projects
    /// every agent's <c>ai:</c> block onto the canonical
    /// <c>execution:{image, agent, provider, model}</c> shape on
    /// <c>AgentDefinitions.Definition</c>. Without this projection the
    /// shared <see cref="Cvoya.Spring.Host.Api.Services.IArtefactAutoStartGate"/>
    /// reads through <see cref="Cvoya.Spring.Core.Execution.IAgentExecutionStore"/>
    /// (which only recognises the modern <c>execution:</c> block) and skips
    /// the gate, leaving every catalog-installed agent stranded in
    /// <see cref="Cvoya.Spring.Core.Lifecycle.LifecycleStatus.Draft"/> while
    /// the parent unit reaches Running. The gate's lifecycle side-effects
    /// are exercised by
    /// <c>Cvoya.Spring.Host.Api.Tests.Services.ArtefactAutoStartGateTests</c>
    /// — here we pin the precondition (Definition shape) that lets the gate
    /// fire end-to-end via the install path.
    /// </summary>
    [Theory]
    [InlineData("hello-world", "greeter")]
    [InlineData("example-simple", "friendly-greeter")]
    [InlineData("example-simple", "polite-greeter")]
    public async Task InstallPackage_AgentDefinition_CarriesCanonicalExecutionBlock(
        string packageName, string agentDisplayName)
    {
        using var fx = BuildFixture();

        var result = await fx.Service.InstallAsync(
            new[] { LoadTarget(packageName) },
            TestContext.Current.CancellationToken);
        result.PackageResults.Single().Status.ShouldBe(PackageInstallOutcome.Active);

        await using var scope = fx.ScopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var agent = await db.AgentDefinitions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(a => a.DisplayName == agentDisplayName,
                TestContext.Current.CancellationToken);
        agent.ShouldNotBeNull($"agent '{agentDisplayName}' must be persisted");

        agent!.Definition.ShouldNotBeNull(
            $"agent '{agentDisplayName}' must carry a Definition document");
        var def = agent.Definition!.Value;
        def.TryGetProperty("execution", out var exec).ShouldBeTrue(
            $"agent '{agentDisplayName}' Definition must carry an 'execution' block " +
            "so the auto-start gate (IAgentExecutionStore.Extract) can read image/model/runtime.");
        exec.ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Object);

        // Each catalog package above uses the ADR-0038 ai: shape with
        // claude-code + anthropic + claude-sonnet-4-6 and the OSS claude
        // base image. The persisted execution block is the one canonical
        // shape (#2634): runtime + structured model{provider, id} + image.
        exec.GetProperty("runtime").GetString().ShouldBe("claude-code");
        var execModel = exec.GetProperty("model");
        execModel.GetProperty("provider").GetString().ShouldBe("anthropic");
        execModel.GetProperty("id").GetString().ShouldBe("claude-sonnet-4-6");
        exec.GetProperty("image").GetString().ShouldStartWith(
            "ghcr.io/cvoya-com/spring-voyage-claude-code-base");

        // The auto-start gate consumes IAgentExecutionStore directly; round-
        // tripping through the same store the gate uses pins that the
        // projected slots actually surface end-to-end (covers any future
        // drift between DbAgentExecutionStore.Extract and the projection).
        var store = scope.ServiceProvider
            .GetRequiredService<Cvoya.Spring.Core.Execution.IAgentExecutionStore>();
        var shape = await store.GetAsync(
            Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(agent.Id),
            TestContext.Current.CancellationToken);
        shape.ShouldNotBeNull(
            $"IAgentExecutionStore.GetAsync must return a populated shape for '{agentDisplayName}' " +
            "— the auto-start gate (#2374 / #2388) reads through this seam.");
        shape!.Runtime.ShouldBe("claude-code");
        shape.Model.ShouldBe(new Cvoya.Spring.Core.Catalog.Model("anthropic", "claude-sonnet-4-6"));
        shape.Image.ShouldStartWith("ghcr.io/cvoya-com/spring-voyage-claude-code-base");
    }

    // ── (3) example-templated ────────────────────────────────────────────

    [Fact]
    public async Task InstallExampleTemplated_HappyPath()
    {
        using var fx = BuildFixture();

        var result = await fx.Service.InstallAsync(
            new[] { LoadTarget("example-templated") },
            TestContext.Current.CancellationToken);

        result.PackageResults.Single().Status.ShouldBe(PackageInstallOutcome.Active);

        await using var scope = fx.ScopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        // Top-level unit: platform-eng (stamped from the engineering-team
        // template — the concrete unit's name wins per ADR-0043 §5d).
        var platformEng = await db.UnitDefinitions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.DisplayName == "platform-eng",
                TestContext.Current.CancellationToken);
        platformEng.ShouldNotBeNull("platform-eng unit row should exist");

        // 5 stamped agents: team-lead + senior-engineer from the template,
        // plus ada / hopper / lovelace declared in the concrete unit's
        // agents/ folder (all three `from: software-engineer`).
        var agents = await db.AgentDefinitions
            .IgnoreQueryFilters()
            .ToListAsync(TestContext.Current.CancellationToken);
        var expectedNames = new[] { "team-lead", "senior-engineer", "ada", "hopper", "lovelace" };
        foreach (var name in expectedNames)
        {
            agents.Any(a => a.DisplayName == name).ShouldBeTrue(
                $"agent '{name}' should be present after stamping");
        }

        // Each stamped software-engineer instance has a fresh Guid — no
        // duplicates across ada/hopper/lovelace (the stamping must produce
        // distinct identities).
        var stamped = agents
            .Where(a => expectedNames.Contains(a.DisplayName))
            .Select(a => a.Id)
            .ToList();
        stamped.Distinct().Count().ShouldBe(stamped.Count,
            "every stamped agent must have a distinct Guid");
    }

    // ── (4) install twice — both succeed ─────────────────────────────────

    [Fact]
    public async Task InstallTwice_SamePackage_BothSucceed()
    {
        using var fx = BuildFixture();

        var first = await fx.Service.InstallAsync(
            new[] { LoadTarget("hello-world") },
            TestContext.Current.CancellationToken);
        first.PackageResults.Single().Status.ShouldBe(PackageInstallOutcome.Active);

        var second = await fx.Service.InstallAsync(
            new[] { LoadTarget("hello-world") },
            TestContext.Current.CancellationToken);
        second.PackageResults.Single().Status.ShouldBe(PackageInstallOutcome.Active);

        first.InstallId.ShouldNotBe(second.InstallId);

        await using var scope = fx.ScopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        // Two distinct unit rows with the same display name (#2310: names are
        // presentation-only; identity is Guid).
        var unitRows = await db.UnitDefinitions
            .IgnoreQueryFilters()
            .Where(u => u.DisplayName == "hello-world")
            .ToListAsync(TestContext.Current.CancellationToken);
        unitRows.Count.ShouldBe(2);
        unitRows[0].Id.ShouldNotBe(unitRows[1].Id);

        // Two distinct agent rows.
        var agentRows = await db.AgentDefinitions
            .IgnoreQueryFilters()
            .Where(a => a.DisplayName == "greeter")
            .ToListAsync(TestContext.Current.CancellationToken);
        agentRows.Count.ShouldBe(2);
        agentRows[0].Id.ShouldNotBe(agentRows[1].Id);
    }

    // ── (5) display-name override ────────────────────────────────────────

    [Fact]
    public async Task InstallWithDisplayName_OverridesUnitName()
    {
        using var fx = BuildFixture();

        var target = LoadTarget("hello-world") with { DisplayName = "my-greeting-team" };
        var result = await fx.Service.InstallAsync(
            new[] { target },
            TestContext.Current.CancellationToken);

        result.PackageResults.Single().Status.ShouldBe(PackageInstallOutcome.Active);

        await using var scope = fx.ScopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        var unit = await db.UnitDefinitions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.DisplayName == "my-greeting-team",
                TestContext.Current.CancellationToken);
        unit.ShouldNotBeNull("the unit should carry the operator-supplied display name");

        // The original "hello-world" display name must NOT appear — the
        // override wins.
        var originalNameRow = await db.UnitDefinitions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.DisplayName == "hello-world",
                TestContext.Current.CancellationToken);
        originalNameRow.ShouldBeNull(
            "the package's declared 'hello-world' name should be replaced by the override");
    }

    // ── (6) ambiguous override rejection ─────────────────────────────────

    [Fact]
    public async Task InstallWithDisplayName_AmbiguousMultipleTopLevel_Rejected()
    {
        using var fx = BuildFixture();

        // Build a small in-fixture package with two top-level units so the
        // ambiguity check fires. Fixture lives under a per-test temp dir so
        // multiple parallel runs don't clobber each other.
        using var multi = BuildMultiTopLevelPackage();

        var target = new InstallTarget(
            PackageName: multi.PackageName,
            Inputs: new Dictionary<string, string>(),
            OriginalYaml: multi.PackageYaml,
            PackageRoot: multi.PackageRoot,
            DisplayName: "rename-me");

        var ex = await Should.ThrowAsync<AmbiguousDisplayNameException>(async () =>
            await fx.Service.InstallAsync(
                new[] { target },
                TestContext.Current.CancellationToken));

        ex.PackageName.ShouldBe(multi.PackageName);
        ex.TopLevelCount.ShouldBeGreaterThanOrEqualTo(2);
    }

    // ── (7) in-flight cross-package `from:` resolves via the recursive layout

    /// <summary>
    /// #2308: <see cref="InFlightBatchCatalogProvider"/> resolves
    /// cross-package <c>from:</c> references between sibling packages in
    /// the same multi-package install batch by walking each in-flight
    /// package's ADR-0043 recursive folder layout — not by constructing
    /// flat <c>&lt;subdir&gt;/&lt;name&gt;.yaml</c> paths (the pre-ADR-0043
    /// layout the bug was using). Two synthetic packages are constructed
    /// in temp dirs:
    /// <list type="bullet">
    ///   <item><description>
    ///     archetype package — ships a <c>UnitTemplate</c> under
    ///     <c>templates/</c> with one nested concrete agent that gets
    ///     stamped under every consumer.
    ///   </description></item>
    ///   <item><description>
    ///     consumer package — ships a <c>Unit</c> whose <c>from:</c>
    ///     points cross-package at the archetype's template.
    ///   </description></item>
    /// </list>
    /// Both are installed in a single batch. Before the fix
    /// <see cref="InFlightBatchCatalogProvider.LoadArtefactYamlAsync"/>
    /// looked for <c>&lt;archetype-root&gt;/units/&lt;tpl-name&gt;.yaml</c>
    /// (the legacy flat layout) and raised "template not found in the
    /// catalog" because the template lives at
    /// <c>&lt;archetype-root&gt;/templates/&lt;tpl-name&gt;/package.yaml</c>.
    /// </summary>
    [Fact]
    public async Task InstallBatch_CrossPackageFromReference_ResolvesViaRecursiveLayout()
    {
        using var fx = BuildFixture();
        using var pkgs = BuildCrossPackageFromBatch();

        var archetypeTarget = new InstallTarget(
            PackageName: pkgs.ArchetypePackageName,
            Inputs: new Dictionary<string, string>(),
            OriginalYaml: pkgs.ArchetypePackageYaml,
            PackageRoot: pkgs.ArchetypePackageRoot);

        var consumerTarget = new InstallTarget(
            PackageName: pkgs.ConsumerPackageName,
            Inputs: new Dictionary<string, string>(),
            OriginalYaml: pkgs.ConsumerPackageYaml,
            PackageRoot: pkgs.ConsumerPackageRoot);

        var result = await fx.Service.InstallAsync(
            new[] { archetypeTarget, consumerTarget },
            TestContext.Current.CancellationToken);

        result.PackageResults.Count.ShouldBe(2);
        foreach (var pr in result.PackageResults)
        {
            pr.Status.ShouldBe(
                PackageInstallOutcome.Active,
                $"package '{pr.PackageName}' should install successfully (error: {pr.ErrorMessage}).");
        }

        await using var scope = fx.ScopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        // The consumer's top-level unit must exist — Phase 2 wrote it after
        // the cross-package template resolved via the in-flight overlay.
        var consumerUnit = await db.UnitDefinitions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.DisplayName == pkgs.ConsumerUnitName,
                TestContext.Current.CancellationToken);
        consumerUnit.ShouldNotBeNull(
            "the consumer unit should be written after the cross-package `from:` resolved through the in-flight overlay");

        // The template's stamped agent child must exist — proves
        // EnumerateNestedArtefactsAsync also walked the in-flight
        // archetype's recursive layout. Before the fix this row would be
        // absent because the resolver raised "template not found" before
        // reaching the stamping step.
        var stampedAgent = await db.AgentDefinitions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(a => a.DisplayName == pkgs.TemplateChildAgentName,
                TestContext.Current.CancellationToken);
        stampedAgent.ShouldNotBeNull(
            "the template's nested child agent should be stamped fresh under the consumer unit");
    }

    // ── (8) malformed manifest fails cleanly ─────────────────────────────

    [Fact]
    public async Task InstallMalformedManifest_FailsCleanly()
    {
        using var fx = BuildFixture();

        // `kind: Pakage` (typo) — ADR-0043 §3 rejects unknown kinds with a
        // precise parse error code.
        var yaml = """
            apiVersion: spring.voyage/v1
            kind: Pakage
            name: typo-pkg
            description: x
            version: 1.0.0
            """;
        var target = new InstallTarget(
            PackageName: "typo-pkg",
            Inputs: new Dictionary<string, string>(),
            OriginalYaml: yaml,
            PackageRoot: null);

        await Should.ThrowAsync<PackageParseException>(async () =>
            await fx.Service.InstallAsync(
                new[] { target },
                TestContext.Current.CancellationToken));
    }

    // ── (9) every in-repo bundle installs cleanly (#2346 gate) ───────────

    /// <summary>
    /// #2346 positive gate: after the bundle audit (Part 1) removed all
    /// invented-namespace <see cref="Cvoya.Spring.Core.Skills.SkillToolRequirement"/>
    /// entries from the in-repo OSS bundles, every package the existing
    /// integration suite already exercises continues to install cleanly
    /// under strict validation. The connector-free packages
    /// (<c>hello-world</c>, <c>example-simple</c>, <c>example-templated</c>)
    /// are the in-suite gate; the other in-repo packages
    /// (<c>spring-voyage-oss</c>, <c>software-engineering</c>,
    /// <c>product-management</c>, <c>research</c>) require connector
    /// bindings and are out of scope for the install-pipeline integration
    /// suite — their bundle decls were audited at the YAML/file level in
    /// Part 1 and the resolver/validator unit suites cover the per-bundle
    /// shape.
    /// </summary>
    [Theory]
    [InlineData("hello-world")]
    [InlineData("example-simple")]
    [InlineData("example-templated")]
    public async Task InstallConnectorFreeInRepoPackage_StrictValidation_Succeeds(string packageName)
    {
        using var fx = BuildFixture();

        var result = await fx.Service.InstallAsync(
            new[] { LoadTarget(packageName) },
            TestContext.Current.CancellationToken);

        result.PackageResults.Single().Status.ShouldBe(
            PackageInstallOutcome.Active,
            $"package '{packageName}' should install cleanly under strict RequiredTool validation");
    }

    // ── (10) strict RequiredTool validation — negative path (#2346) ──────

    /// <summary>
    /// #2346 negative gate: a synthetic package whose unit references a
    /// skill bundle declaring <c>nonexistent.foo</c> — a namespace no
    /// <see cref="Cvoya.Spring.Core.Skills.ISkillRegistry"/> exposes and
    /// which the image-tier
    /// <see cref="Cvoya.Spring.Core.Skills.IImageToolsReader"/> does not
    /// surface either — fails install with a
    /// <see cref="Cvoya.Spring.Core.Skills.SkillBundleValidationException"/>
    /// carrying a
    /// <see cref="Cvoya.Spring.Core.Skills.SkillBundleValidationProblemReason.ToolNotAvailable"/>
    /// problem. The endpoint layer maps that exception to a 400 with
    /// <c>code: "RequiredToolUnresolved"</c> (see
    /// <c>PackageInstallEndpoints.ExecuteInstallAsync</c>'s catch
    /// block).
    /// </summary>
    [Fact]
    public async Task InstallBundleWithUnresolvedRequiredTool_FailsWithToolNotAvailable()
    {
        using var pkg = BuildPackageWithSkillBundle("nonexistent.foo");
        using var fx = BuildFixtureForRoot(pkg.Root);
        await SeedSkillBundleBindingAsync(fx, pkg.PackageName);

        var target = new InstallTarget(
            PackageName: pkg.PackageName,
            Inputs: new Dictionary<string, string>(),
            OriginalYaml: pkg.PackageYaml,
            PackageRoot: pkg.PackageRoot);

        var ex = await Should.ThrowAsync<Cvoya.Spring.Core.Skills.SkillBundleValidationException>(
            () => fx.Service.InstallAsync(
                new[] { target },
                TestContext.Current.CancellationToken));

        ex.Problems.ShouldContain(p =>
            p.Reason == Cvoya.Spring.Core.Skills.SkillBundleValidationProblemReason.ToolNotAvailable
            && p.ToolName == "nonexistent.foo");
    }

    /// <summary>
    /// #2346 positive companion to the synthetic-package negative: a
    /// bundle that declares a tool in the <c>sv.*</c> namespace — exposed
    /// by the platform's
    /// <see cref="Cvoya.Spring.Dapr.Skills.SvDirectorySkillRegistry"/> —
    /// installs cleanly under strict validation.
    /// </summary>
    [Fact]
    public async Task InstallBundleWithSvNamespaceRequiredTool_Succeeds()
    {
        using var pkg = BuildPackageWithSkillBundle("sv.directory.get_self");
        using var fx = BuildFixtureForRoot(pkg.Root);
        await SeedSkillBundleBindingAsync(fx, pkg.PackageName);

        var target = new InstallTarget(
            PackageName: pkg.PackageName,
            Inputs: new Dictionary<string, string>(),
            OriginalYaml: pkg.PackageYaml,
            PackageRoot: pkg.PackageRoot);

        var result = await fx.Service.InstallAsync(
            new[] { target },
            TestContext.Current.CancellationToken);

        var pkgResult = result.PackageResults.Single();
        pkgResult.Status.ShouldBe(
            PackageInstallOutcome.Active,
            $"install failed with: {pkgResult.ErrorMessage}");
    }

    /// <summary>
    /// Seeds an enabled <c>tenant_skill_bundle_bindings</c> row so the
    /// tenant-filtering bundle resolver lets the synthetic package's
    /// bundle through. Without this the resolver wrapper rejects the
    /// lookup before the validator even runs.
    /// </summary>
    private static async Task SeedSkillBundleBindingAsync(Fixture fx, string packageName)
    {
        await using var scope = fx.ScopeFactory.CreateAsyncScope();
        var bindingService = scope.ServiceProvider
            .GetRequiredService<Cvoya.Spring.Core.Skills.ITenantSkillBundleBindingService>();
        await bindingService.BindAsync(packageName, enabled: true, TestContext.Current.CancellationToken);
    }

    // ── Fixture helpers ──────────────────────────────────────────────────

    private sealed class Fixture : IDisposable
    {
        public required PackageInstallService Service { get; init; }
        public required IServiceScopeFactory ScopeFactory { get; init; }
        public required ServiceProvider Provider { get; init; }

        public void Dispose() => Provider.Dispose();
    }

    private Fixture BuildFixture() => BuildFixtureForRoot(_packagesRoot);

    private Fixture BuildFixtureForRoot(string packagesRoot)
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // UnitCreationService depends on IHttpContextAccessor for the
        // creator-grant path (line 684 of UnitCreationService.cs).
        // Register the standard implementation; the tests don't push a
        // request through HTTP so the accessor's Current is null and the
        // service falls back to its anonymous-creator branch.
        services.AddHttpContextAccessor();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Tenancy:BootstrapDefaultTenant"] = "false",
                ["Packages:Root"] = packagesRoot,
                ["Skills:PackagesRoot"] = packagesRoot,
            })
            .Build();
        services.AddSingleton<IConfiguration>(config);

        // In-memory EF, fresh per test.
        var dbName = $"sv-install-{Guid.NewGuid():N}";
        services.AddDbContext<SpringDbContext>(opts => opts
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w =>
                w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning)));

        // Substitute the actor proxy factory with a fake that delegates
        // IUnitActor.AddMemberAsync to the real IUnitMembershipCoordinator
        // (resolved from DI after the provider is built) so unit_memberships
        // rows are actually written. Every other actor method is a no-op
        // substitute — the install pipeline's directory-state assertions
        // don't depend on actor-state writes.
        var actorProxyFactory = new TestActorProxyFactory();
        services.AddSingleton<IActorProxyFactory>(actorProxyFactory);

        // Wire the Dapr DI graph — pulls in IUnitMemberGraphStore,
        // IUnitMembershipCoordinator, IDirectoryService, etc. This also
        // registers an IActorProxyFactory implementation, but TryAdd
        // semantics mean ours wins.
        services.AddCvoyaSpringDapr(config);

        // Replace the Dapr state store with an in-memory fake AFTER the
        // Dapr DI graph wires its real DaprStateStore (Dapr uses
        // AddSingleton, not TryAdd, so an earlier registration would lose
        // the race). Install paths that write skill-bundle blobs through
        // the state store (units with `ai.skills:` entries) need this — the
        // real DaprStateStore needs a live sidecar the integration suite
        // doesn't stand up.
        services.RemoveAll<Cvoya.Spring.Core.State.IStateStore>();
        services.AddSingleton<Cvoya.Spring.Core.State.IStateStore, FakeStateStore>();

        // Wire the Host.Api install pipeline + catalog. Pinning
        // PackageCatalogOptions before AddCvoyaSpringApiServices ensures
        // the catalog points at the in-repo packages/ root (the
        // extension's auto-discovery is best-effort).
        services.AddSingleton<PackageCatalogOptions>(_ => new PackageCatalogOptions
        {
            Root = packagesRoot,
        });
        services.AddCvoyaSpringApiServices(config);

        var sp = services.BuildServiceProvider();
        _providers.Add(sp);
        actorProxyFactory.Bind(sp);

        return new Fixture
        {
            Service = (PackageInstallService)sp.GetRequiredService<IPackageInstallService>(),
            ScopeFactory = sp.GetRequiredService<IServiceScopeFactory>(),
            Provider = sp,
        };
    }

    private InstallTarget LoadTarget(string packageName)
    {
        var packageRoot = Path.Combine(_packagesRoot, packageName);
        var yaml = File.ReadAllText(Path.Combine(packageRoot, "package.yaml"));
        return new InstallTarget(
            PackageName: packageName,
            Inputs: new Dictionary<string, string>(),
            OriginalYaml: yaml,
            PackageRoot: packageRoot);
    }

    /// <summary>
    /// Builds a synthetic single-unit package on temp disk whose unit
    /// references one skill bundle declaring a single
    /// <see cref="Cvoya.Spring.Core.Skills.SkillToolRequirement"/> with
    /// the supplied <paramref name="toolName"/>. Layout follows the
    /// ADR-0043 recursive shape (<c>skills/&lt;skill&gt;/&lt;skill&gt;.md</c>
    /// + adjacent <c>&lt;skill&gt;.tools.json</c>) so the package walker
    /// accepts it and the file-system bundle resolver finds the prompt.
    /// </summary>
    private static StrictValidationPackage BuildPackageWithSkillBundle(string toolName)
    {
        var stamp = Guid.NewGuid().ToString("N")[..8];
        var root = Path.Combine(
            Path.GetTempPath(),
            "spring-voyage-tests",
            $"strict-validation-{stamp}");
        Directory.CreateDirectory(root);

        var packageName = $"strict-validation-{stamp}";
        var skillName = "synthetic-skill";
        var unitName = $"unit-{stamp}";

        // Package root.
        var packageRoot = Path.Combine(root, packageName);
        Directory.CreateDirectory(packageRoot);
        var packageYaml = $"""
            apiVersion: spring.voyage/v1
            kind: Package
            name: {packageName}
            description: Synthetic single-unit package for the #2346 strict-validation tests.
            version: 1.0.0
            """;
        File.WriteAllText(Path.Combine(packageRoot, "package.yaml"), packageYaml);

        // Unit that references the skill bundle.
        var unitDir = Path.Combine(packageRoot, "units", unitName);
        Directory.CreateDirectory(unitDir);
        File.WriteAllText(Path.Combine(unitDir, "package.yaml"), $"""
            apiVersion: spring.voyage/v1
            kind: Unit
            name: {unitName}
            description: Unit referencing a synthetic skill bundle.
            ai:
              runtime: claude-code
              model:
                provider: anthropic
                id: claude-sonnet-4-6
              skills:
                - package: {packageName}
                  skill: {skillName}
            instructions: |
              You exercise the strict skill-bundle validator.
            execution:
              image: ghcr.io/cvoya-com/spring-voyage-claude-code-base:latest
            """);

        // Skill bundle: prompt + tools.json. ADR-0043 recursive layout —
        // skills/<skill>/<skill>.md + skills/<skill>/<skill>.tools.json.
        // A `package.yaml` next to the prompt makes the package walker
        // treat the folder as a Skill artefact rather than rejecting it
        // as the legacy flat layout.
        var skillDir = Path.Combine(packageRoot, "skills", skillName);
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "package.yaml"), $"""
            apiVersion: spring.voyage/v1
            kind: Skill
            name: {skillName}
            description: Synthetic skill that exercises strict tool validation.
            """);
        File.WriteAllText(Path.Combine(skillDir, $"{skillName}.md"),
            "## Synthetic skill prompt");
        File.WriteAllText(Path.Combine(skillDir, $"{skillName}.tools.json"), $$"""
            [
              {
                "name": "{{toolName}}",
                "description": "Tool exercised by the #2346 strict-validation tests.",
                "parameters": { "type": "object" }
              }
            ]
            """);

        return new StrictValidationPackage
        {
            Root = root,
            PackageRoot = packageRoot,
            PackageName = packageName,
            PackageYaml = packageYaml,
        };
    }

    /// <summary>
    /// In-memory <see cref="Cvoya.Spring.Core.State.IStateStore"/>
    /// substitute for the integration suite — the real
    /// <see cref="Cvoya.Spring.Dapr.State.DaprStateStore"/> needs a live
    /// Dapr sidecar, which the test host doesn't run. Persists JSON-
    /// serialised values per-key so install paths that round-trip skill
    /// bundles (<see cref="Cvoya.Spring.Dapr.Skills.StateStoreBackedUnitSkillBundleStore"/>)
    /// stay green without standing up Dapr.
    /// </summary>
    private sealed class FakeStateStore : Cvoya.Spring.Core.State.IStateStore
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _values = new();

        public Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
        {
            if (!_values.TryGetValue(key, out var json))
            {
                return Task.FromResult<T?>(default);
            }
            var value = System.Text.Json.JsonSerializer.Deserialize<T>(json);
            return Task.FromResult(value);
        }

        public Task SetAsync<T>(string key, T value, CancellationToken ct = default)
        {
            _values[key] = System.Text.Json.JsonSerializer.Serialize(value);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string key, CancellationToken ct = default)
        {
            _values.TryRemove(key, out _);
            return Task.CompletedTask;
        }

        public Task<bool> ContainsAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(_values.ContainsKey(key));
    }

    private sealed class StrictValidationPackage : IDisposable
    {
        public required string Root { get; init; }
        public required string PackageRoot { get; init; }
        public required string PackageName { get; init; }
        public required string PackageYaml { get; init; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch { /* best effort */ }
        }
    }

    private sealed class MultiTopLevelPackage : IDisposable
    {
        public required string PackageRoot { get; init; }
        public required string PackageName { get; init; }
        public required string PackageYaml { get; init; }

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

    private static MultiTopLevelPackage BuildMultiTopLevelPackage()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "spring-voyage-tests",
            $"multi-top-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        const string packageName = "multi-top-level";
        var packageYaml = $"""
            apiVersion: spring.voyage/v1
            kind: Package
            name: {packageName}
            description: Two top-level units — used to trigger the AmbiguousDisplayName check.
            version: 1.0.0
            """;
        File.WriteAllText(Path.Combine(root, "package.yaml"), packageYaml);

        // Two top-level units side-by-side under units/.
        var oneDir = Path.Combine(root, "units", "alpha");
        Directory.CreateDirectory(oneDir);
        File.WriteAllText(Path.Combine(oneDir, "package.yaml"), """
            apiVersion: spring.voyage/v1
            kind: Unit
            name: alpha
            description: First top-level unit.
            """);

        var twoDir = Path.Combine(root, "units", "beta");
        Directory.CreateDirectory(twoDir);
        File.WriteAllText(Path.Combine(twoDir, "package.yaml"), """
            apiVersion: spring.voyage/v1
            kind: Unit
            name: beta
            description: Second top-level unit.
            """);

        return new MultiTopLevelPackage
        {
            PackageRoot = root,
            PackageName = packageName,
            PackageYaml = packageYaml,
        };
    }

    private sealed class CrossPackageFromBatch : IDisposable
    {
        public required string Root { get; init; }
        public required string ArchetypePackageRoot { get; init; }
        public required string ConsumerPackageRoot { get; init; }
        public required string ArchetypePackageName { get; init; }
        public required string ConsumerPackageName { get; init; }
        public required string ArchetypePackageYaml { get; init; }
        public required string ConsumerPackageYaml { get; init; }
        public required string ConsumerUnitName { get; init; }
        public required string TemplateChildAgentName { get; init; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Builds a pair of synthetic ADR-0043 packages on disk that exercise
    /// the in-flight cross-package <c>from:</c> path (#2308). Both
    /// packages are connector-free so the install pipeline doesn't need a
    /// binding pre-flight to succeed. Names are namespaced with a fresh
    /// Guid suffix so parallel test runs don't collide on display-name
    /// lookups in the in-memory EF database.
    /// </summary>
    private static CrossPackageFromBatch BuildCrossPackageFromBatch()
    {
        var stamp = Guid.NewGuid().ToString("N")[..8];
        var root = Path.Combine(
            Path.GetTempPath(),
            "spring-voyage-tests",
            $"xpkg-from-{stamp}");
        Directory.CreateDirectory(root);

        var archetypeName = $"xpkg-archetype-{stamp}";
        var consumerName = $"xpkg-consumer-{stamp}";
        var templateName = $"engineering-team-tpl-{stamp}";
        var consumerUnitName = $"platform-team-{stamp}";
        var templateChildAgentName = $"team-lead-tpl-{stamp}";

        // ── Archetype package — ships a UnitTemplate with a nested Agent.
        var archetypeRoot = Path.Combine(root, archetypeName);
        Directory.CreateDirectory(archetypeRoot);
        var archetypePackageYaml = $"""
            apiVersion: spring.voyage/v1
            kind: Package
            name: {archetypeName}
            description: Archetype library for the #2308 cross-package in-flight test.
            version: 1.0.0
            """;
        File.WriteAllText(
            Path.Combine(archetypeRoot, "package.yaml"), archetypePackageYaml);

        var templateDir = Path.Combine(archetypeRoot, "templates", templateName);
        Directory.CreateDirectory(templateDir);
        File.WriteAllText(Path.Combine(templateDir, "package.yaml"), $"""
            apiVersion: spring.voyage/v1
            kind: UnitTemplate
            name: {templateName}
            description: Engineering-team archetype used by the #2308 in-flight regression test.
            ai:
              runtime: claude-code
              model:
                provider: anthropic
                id: claude-sonnet-4-6
            instructions: |
              You orchestrate an engineering team derived from the archetype.
            members:
              - human:
                  roles: [owner]
                  notifications: ["escalation"]
            execution:
              image: ghcr.io/cvoya-com/spring-voyage-claude-code-base:latest
            policies:
              communication: through-unit
              work_assignment: capability-match
              expertise_sharing: advertise
              initiative:
                max_level: attentive
                max_actions_per_hour: 10
            """);

        var teamLeadDir = Path.Combine(templateDir, "agents", templateChildAgentName);
        Directory.CreateDirectory(teamLeadDir);
        File.WriteAllText(Path.Combine(teamLeadDir, "package.yaml"), $"""
            apiVersion: spring.voyage/v1
            kind: Agent
            name: {templateChildAgentName}
            description: Team lead stamped under every archetype instance.
            role: team-lead
            capabilities: ["design-review"]
            ai:
              runtime: claude-code
              model:
                provider: anthropic
                id: claude-sonnet-4-6
              environment:
                image: ghcr.io/cvoya-com/spring-voyage-claude-code-base:latest
            instructions: |
              You are the team lead.
            expertise:
              - domain: design-review
                level: expert
            """);

        // ── Consumer package — declares a Unit with cross-package `from:`.
        var consumerRoot = Path.Combine(root, consumerName);
        Directory.CreateDirectory(consumerRoot);
        var consumerPackageYaml = $"""
            apiVersion: spring.voyage/v1
            kind: Package
            name: {consumerName}
            description: Consumer that cross-references the archetype's template (in-flight, #2308).
            version: 1.0.0
            """;
        File.WriteAllText(
            Path.Combine(consumerRoot, "package.yaml"), consumerPackageYaml);

        var consumerUnitDir = Path.Combine(consumerRoot, "units", consumerUnitName);
        Directory.CreateDirectory(consumerUnitDir);
        File.WriteAllText(Path.Combine(consumerUnitDir, "package.yaml"), $"""
            apiVersion: spring.voyage/v1
            kind: Unit
            name: {consumerUnitName}
            description: Concrete unit stamped from the cross-package archetype.
            from: {archetypeName}/{templateName}
            """);

        return new CrossPackageFromBatch
        {
            Root = root,
            ArchetypePackageRoot = archetypeRoot,
            ConsumerPackageRoot = consumerRoot,
            ArchetypePackageName = archetypeName,
            ConsumerPackageName = consumerName,
            ArchetypePackageYaml = archetypePackageYaml,
            ConsumerPackageYaml = consumerPackageYaml,
            ConsumerUnitName = consumerUnitName,
            TemplateChildAgentName = templateChildAgentName,
        };
    }

    private static string LocateRepoPackagesRoot()
    {
        // Mirror the LocatePackageRoot pattern used by HelloWorldPackageTests.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir.FullName, "packages");
            if (Directory.Exists(candidate)
                && File.Exists(Path.Combine(candidate, "hello-world", "package.yaml")))
            {
                return candidate;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not locate the repo's packages/ directory by walking up from the test binary location.");
    }

    // ── Test actor proxy factory ─────────────────────────────────────────

    /// <summary>
    /// Test substitute for <see cref="IActorProxyFactory"/>. Returns
    /// NSubstitute proxies for every actor interface; for
    /// <see cref="IUnitActor"/> the proxy's <c>AddMemberAsync</c> is wired
    /// to delegate to the real <see cref="IUnitMembershipCoordinator"/> so
    /// the install pipeline writes <c>unit_memberships</c> rows through
    /// the same path the runtime uses, without standing up a live Dapr
    /// placement.
    /// </summary>
    private sealed class TestActorProxyFactory : IActorProxyFactory
    {
        private IServiceProvider? _services;

        public void Bind(IServiceProvider services) => _services = services;

        public TActorInterface CreateActorProxy<TActorInterface>(
            ActorId actorId,
            string actorType,
            ActorProxyOptions? options = null)
            where TActorInterface : IActor
        {
            ArgumentNullException.ThrowIfNull(actorId);

            if (typeof(TActorInterface) == typeof(IUnitActor))
            {
                var coordinator = _services!.GetRequiredService<IUnitMembershipCoordinator>();
                var unitId = ParseGuid(actorId.GetId());
                var unitAddress = Address.ForIdentity(Address.UnitScheme, unitId);

                var sub = (IUnitActor)Substitute.For(new[] { typeof(IUnitActor) }, Array.Empty<object>());
                sub.AddMemberAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
                    .Returns(call => coordinator.AddMemberAsync(
                        unitId: unitId,
                        unitAddress: unitAddress,
                        member: call.ArgAt<Address>(0),
                        emitStateChanged: (_, _, _) => Task.CompletedTask,
                        cancellationToken: call.ArgAt<CancellationToken>(1)));
                return (TActorInterface)(object)sub;
            }

            // Every other actor interface: a bare substitute is enough —
            // the install pipeline's directory-state assertions don't
            // depend on those methods doing anything. Use the array-typed
            // Substitute.For so the generic constraint check stays happy
            // (TActorInterface is IActor, not necessarily `class`).
            return (TActorInterface)Substitute.For(new[] { typeof(TActorInterface) }, Array.Empty<object>());
        }

        public ActorProxy Create(
            ActorId actorId,
            string actorType,
            ActorProxyOptions? options = null) =>
            throw new NotSupportedException(
                "The test actor proxy factory only supports typed CreateActorProxy<T> calls.");

        public object CreateActorProxy(
            ActorId actorId,
            Type actorInterfaceType,
            string actorType,
            ActorProxyOptions? options = null) =>
            throw new NotSupportedException(
                "The test actor proxy factory only supports the generic overload.");

        private static Guid ParseGuid(string actorId) =>
            GuidFormatter.TryParse(actorId, out var guid) ? guid : Guid.Parse(actorId);
    }
}
