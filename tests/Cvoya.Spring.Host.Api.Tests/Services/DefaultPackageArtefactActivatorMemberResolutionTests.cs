// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Services;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Tenancy;
using Cvoya.Spring.Host.Api.Models;
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
/// Tests for <see cref="DefaultPackageArtefactActivator"/>'s umbrella-member
/// resolution path (issue #1664). The activator must rewrite each
/// <c>members[]</c> reference into the canonical Guid form of the resolved
/// peer artefact BEFORE forwarding the manifest to <see cref="IUnitCreationService"/>.
/// Without that rewrite the creation service's slow-path display-name lookup
/// silently mints fresh Guids on miss and the install lands every child at
/// the top of the Explorer tree instead of nested under the umbrella.
/// </summary>
public class DefaultPackageArtefactActivatorMemberResolutionTests
{
    private const string UmbrellaYaml = """
        unit:
          name: spring-voyage-oss
          description: Umbrella unit
          members:
            - unit: sv-oss-software-engineering
            - unit: sv-oss-design
        """;

    private const string UmbrellaWithMissingMemberYaml = """
        unit:
          name: standalone-umbrella
          description: Umbrella naming a non-existent member
          members:
            - unit: ghost-member
        """;

    private const string UmbrellaWithCrossPackageGuidYaml = """
        unit:
          name: hybrid-umbrella
          description: Mixes batch peer + already-installed peer
          members:
            - unit: in-batch-peer
            - unit: aabbccdd11ee22ff33445566778899aa
        """;

    [Fact]
    public async Task ActivateAsync_UmbrellaWithBatchPeers_RewritesMembersToBatchGuids()
    {
        // Phase 1 already minted symbol Guids for both child units. The
        // activator must rewrite the `members:` entries so the unit creation
        // service's loop takes the Guid fast path and forwards the same
        // identities to UnitActor.AddMemberAsync.
        var symbolMap = new LocalSymbolMap();
        var seGuid = symbolMap.GetOrMint(ArtefactKind.Unit, "sv-oss-software-engineering");
        var designGuid = symbolMap.GetOrMint(ArtefactKind.Unit, "sv-oss-design");
        var umbrellaGuid = symbolMap.GetOrMint(ArtefactKind.Unit, "spring-voyage-oss");

        var fixture = new Fixture();
        var activator = fixture.Build();

        await activator.ActivateAsync(
            "pkg-oss",
            new ResolvedArtefact
            {
                Name = "spring-voyage-oss",
                Kind = ArtefactKind.Unit,
                Content = UmbrellaYaml,
            },
            Guid.NewGuid(),
            symbolMap,
            cancellationToken: TestContext.Current.CancellationToken);

        // The creation service receives the manifest with members rewritten
        // to canonical Guid form — that is the wire shape the bug fix
        // depends on.
        fixture.CapturedManifest.ShouldNotBeNull();
        fixture.CapturedManifest!.Members.ShouldNotBeNull();
        fixture.CapturedManifest.Members!.Count.ShouldBe(2);
        fixture.CapturedManifest.Members[0].Unit.ShouldBe(GuidFormatter.Format(seGuid));
        fixture.CapturedManifest.Members[1].Unit.ShouldBe(GuidFormatter.Format(designGuid));

        // The umbrella's own actor identity comes from the same symbol map.
        fixture.CapturedOverrides!.ActorId.ShouldBe(umbrellaGuid);
    }

    [Fact]
    public async Task ActivateAsync_MemberAlreadyInDirectory_FallsBackToDisplayNameLookup()
    {
        // Members not in the install batch's symbol map but already
        // registered in the directory must still resolve — that's how a
        // package can compose with a unit installed in a prior batch.
        var symbolMap = new LocalSymbolMap();
        var umbrellaGuid = symbolMap.GetOrMint(ArtefactKind.Unit, "umbrella");
        // sv-oss-software-engineering is NOT in the symbol map — simulate
        // a previously-installed unit.
        var preExistingGuid = Guid.NewGuid();

        var fixture = new Fixture();
        fixture.DirectoryEntries.Add(new DirectoryEntry(
            Address.ForIdentity("unit", preExistingGuid),
            preExistingGuid,
            "sv-oss-software-engineering",
            "pre-existing",
            null,
            DateTimeOffset.UtcNow));
        var activator = fixture.Build();

        await activator.ActivateAsync(
            "pkg-umbrella",
            new ResolvedArtefact
            {
                Name = "umbrella",
                Kind = ArtefactKind.Unit,
                Content = """
                    unit:
                      name: umbrella
                      members:
                        - unit: sv-oss-software-engineering
                    """,
            },
            Guid.NewGuid(),
            symbolMap,
            cancellationToken: TestContext.Current.CancellationToken);

        fixture.CapturedManifest.ShouldNotBeNull();
        fixture.CapturedManifest!.Members![0].Unit
            .ShouldBe(GuidFormatter.Format(preExistingGuid));
        fixture.CapturedOverrides!.ActorId.ShouldBe(umbrellaGuid);
    }

    [Fact]
    public async Task ActivateAsync_MemberNeitherInBatchNorInDirectory_ThrowsUmbrellaMemberNotFound()
    {
        // The umbrella names a member that is not in the install batch's
        // symbol map and not in the tenant directory. Pre-#1664 the
        // creation service silently minted a fresh Guid and the install
        // appeared to succeed but left every child stranded at top level.
        // Post-fix: throw a precise exception that surfaces through the
        // package-install Phase-2 failure path.
        var symbolMap = new LocalSymbolMap();
        symbolMap.GetOrMint(ArtefactKind.Unit, "standalone-umbrella");
        // Note: ghost-member is intentionally NOT in the symbol map.

        var fixture = new Fixture();
        // Directory is empty — no fallback resolution.
        var activator = fixture.Build();

        var ex = await Should.ThrowAsync<UmbrellaMemberNotFoundException>(async () =>
            await activator.ActivateAsync(
                "pkg-orphan",
                new ResolvedArtefact
                {
                    Name = "standalone-umbrella",
                    Kind = ArtefactKind.Unit,
                    Content = UmbrellaWithMissingMemberYaml,
                },
                Guid.NewGuid(),
                symbolMap,
                cancellationToken: TestContext.Current.CancellationToken));

        ex.Reference.ShouldBe("ghost-member");
        ex.Scheme.ShouldBe("unit");
        ex.Message.ShouldContain("UmbrellaMemberNotFound");
        ex.Message.ShouldContain("ghost-member");

        // The creation service must not have been called — failing fast
        // means no half-installed umbrella to clean up.
        fixture.CapturedManifest.ShouldBeNull();
    }

    [Fact]
    public async Task ActivateAsync_MemberAsCrossPackageGuid_PassesThroughUnchanged()
    {
        // A 32-char no-dash hex value in the members list is a cross-
        // package Guid reference. LocalSymbolMap.TryResolve probes the
        // Guid form before the dictionary so cross-package refs ride
        // through the resolver unchanged.
        var symbolMap = new LocalSymbolMap();
        symbolMap.GetOrMint(ArtefactKind.Unit, "hybrid-umbrella");
        var inBatchGuid = symbolMap.GetOrMint(ArtefactKind.Unit, "in-batch-peer");

        var fixture = new Fixture();
        var activator = fixture.Build();

        await activator.ActivateAsync(
            "pkg-hybrid",
            new ResolvedArtefact
            {
                Name = "hybrid-umbrella",
                Kind = ArtefactKind.Unit,
                Content = UmbrellaWithCrossPackageGuidYaml,
            },
            Guid.NewGuid(),
            symbolMap,
            cancellationToken: TestContext.Current.CancellationToken);

        fixture.CapturedManifest.ShouldNotBeNull();
        fixture.CapturedManifest!.Members!.Count.ShouldBe(2);
        fixture.CapturedManifest.Members[0].Unit
            .ShouldBe(GuidFormatter.Format(inBatchGuid));
        // The cross-package Guid is preserved as-is (passes TryResolve via
        // GuidFormatter.TryParse which accepts no-dash hex).
        fixture.CapturedManifest.Members[1].Unit
            .ShouldBe("aabbccdd11ee22ff33445566778899aa");
    }

    [Fact]
    public async Task ActivateAsync_AgentMember_ResolvesViaSymbolMap()
    {
        // An AgentPackage installed alongside a UnitPackage typically has
        // the unit's `members:` list reference agent peers by symbol name.
        // The same resolution path must work for the `agent:` field.
        var symbolMap = new LocalSymbolMap();
        symbolMap.GetOrMint(ArtefactKind.Unit, "team-unit");
        var architectGuid = symbolMap.GetOrMint(ArtefactKind.Agent, "architect");

        var fixture = new Fixture();
        var activator = fixture.Build();

        await activator.ActivateAsync(
            "pkg-team",
            new ResolvedArtefact
            {
                Name = "team-unit",
                Kind = ArtefactKind.Unit,
                Content = """
                    unit:
                      name: team-unit
                      members:
                        - agent: architect
                    """,
            },
            Guid.NewGuid(),
            symbolMap,
            cancellationToken: TestContext.Current.CancellationToken);

        fixture.CapturedManifest!.Members![0].Agent
            .ShouldBe(GuidFormatter.Format(architectGuid));
    }

    [Fact]
    public async Task InstallAsync_OssPackage_UmbrellaMembersResolveToBatchSubunitGuids()
    {
        // End-to-end integration test against the live `packages/spring-voyage-oss/`
        // directory. Wires the real DefaultPackageArtefactActivator into a
        // PackageInstallService backed by an in-memory DB, with the leaf
        // unit-creation step substituted so we don't need actor/dapr/HTTP
        // infrastructure. After install, every umbrella `members[]` reference
        // forwarded to the unit creation service must be the canonical Guid
        // of the corresponding sub-unit's pre-minted symbol-map entry — the
        // exact wire shape that drives unit_subunit_memberships rows whose
        // child_id matches a real unit_definitions.id.
        var packageRoot = ResolveOssPackageRoot();
        var packageYaml = File.ReadAllText(Path.Combine(packageRoot, "package.yaml"));

        var capturedCalls = new List<(string UnitName, IReadOnlyList<MemberManifest> Members, Guid? ActorId)>();
        var unitCreation = Substitute.For<IUnitCreationService>();
        unitCreation.CreateFromManifestAsync(
                Arg.Any<UnitManifest>(),
                Arg.Any<UnitCreationOverrides>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<UnitConnectorBindingRequest?>())
            .Returns(call =>
            {
                var manifest = call.Arg<UnitManifest>();
                var overrides = call.Arg<UnitCreationOverrides>();
                capturedCalls.Add((
                    manifest.Name ?? "<no-name>",
                    manifest.Members?.ToList() ?? new List<MemberManifest>(),
                    overrides.ActorId));
                return Task.FromResult(new UnitCreationResult(
                    new UnitResponse(
                        overrides.ActorId ?? Guid.NewGuid(),
                        "n",
                        manifest.Name ?? "x",
                        manifest.Description ?? string.Empty,
                        DateTimeOffset.UtcNow,
                        Cvoya.Spring.Core.Units.UnitStatus.Draft,
                        null, null),
                    Array.Empty<string>(),
                    0));
            });

        var directoryEntries = new List<DirectoryEntry>();
        var directory = Substitute.For<IDirectoryService>();
        directory.ListAllAsync(Arg.Any<CancellationToken>())
            .Returns(_ => (IReadOnlyList<DirectoryEntry>)directoryEntries.ToList());
        directory.ResolveAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns((DirectoryEntry?)null);

        // In-memory DB scope factory.
        var services = new ServiceCollection();
        services.AddSingleton<ITenantContext>(new StaticTenantContext(
            new Guid("aaaaaaaa-1111-2222-3333-aaaaaaaaaaaa")));
        var dbName = $"oss-install-{Guid.NewGuid():N}";
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

        var activator = new DefaultPackageArtefactActivator(
            unitCreation, directory, scopeFactory,
            NullLogger<DefaultPackageArtefactActivator>.Instance);

        var installer = new PackageInstallService(
            scopeFactory, directory, activator,
            NullLogger<PackageInstallService>.Instance);

        // #1670: github_* inputs migrated to a `connectors:` declaration on
        // the package manifest. The install target now passes the operator-
        // supplied package-scope connector binding instead of typed inputs.
        var inputs = new Dictionary<string, string>();
        var pkgBindings = new Dictionary<string, ConnectorBinding>(StringComparer.OrdinalIgnoreCase)
        {
            ["github"] = new ConnectorBinding(
                "github",
                System.Text.Json.JsonDocument.Parse(
                    "{\"owner\":\"cvoya-com\",\"repo\":\"spring-voyage-oss\",\"installation-id\":12345}").RootElement),
        };
        var target = new InstallTarget(
            "spring-voyage-oss", inputs, packageYaml, packageRoot,
            PackageBindings: pkgBindings);

        var result = await installer.InstallAsync(
            new[] { target }, TestContext.Current.CancellationToken);

        result.PackageResults.ShouldHaveSingleItem();
        result.PackageResults[0].Status.ShouldBe(PackageInstallOutcome.Active,
            customMessage: $"Install failed: {result.PackageResults[0].ErrorMessage}");

        // The activator forwarded one CreateFromManifestAsync call per unit
        // — the four sub-units plus the umbrella.
        capturedCalls.Count.ShouldBe(5,
            customMessage: $"Captured: [{string.Join(", ", capturedCalls.Select(c => c.UnitName))}]");

        // Build a lookup of slug → minted Guid by reading the actor-id each
        // call received. The umbrella's name in the manifest is "Spring
        // Voyage OSS" but its symbol (and pre-minted Guid) is keyed by the
        // package slug "spring-voyage-oss" — the activator's actorId
        // override carries that pre-minted Guid.
        var byManifestName = capturedCalls.ToDictionary(c => c.UnitName, c => c.ActorId!.Value);

        // The umbrella's call carries the four sub-unit member references.
        var umbrellaCall = capturedCalls.Single(c => c.UnitName == "Spring Voyage OSS");
        umbrellaCall.Members.Count.ShouldBe(4);

        // Cross-reference: every member's `Unit` field is the canonical
        // Guid of one of the four sub-unit calls. Sub-unit display names
        // come from the YAML files: "Software Engineering", "Design",
        // "Product Management", "Program Management".
        var expectedSubunitGuids = new[]
        {
            byManifestName["Software Engineering"],
            byManifestName["Design"],
            byManifestName["Product Management"],
            byManifestName["Program Management"],
        };
        var actualMemberGuids = umbrellaCall.Members
            .Select(m => Guid.Parse(m.Unit!))
            .OrderBy(g => g)
            .ToArray();
        Array.Sort(expectedSubunitGuids);
        actualMemberGuids.ShouldBe(expectedSubunitGuids);
    }

    private static string ResolveOssPackageRoot()
    {
        // Walk up from the test bin directory until we find AGENTS.md
        // (the repo root marker), then resolve packages/spring-voyage-oss.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AGENTS.md")))
            {
                var candidate = Path.Combine(dir.FullName, "packages", "spring-voyage-oss");
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not locate packages/spring-voyage-oss/ from test base directory.");
    }

    [Fact]
    public async Task ActivateAsync_NoMembers_DoesNotConsultDirectory()
    {
        // A unit with no members must not pay for an unnecessary directory
        // round-trip — the resolver loads the listing lazily.
        var symbolMap = new LocalSymbolMap();
        symbolMap.GetOrMint(ArtefactKind.Unit, "lonely-unit");

        var fixture = new Fixture();
        var activator = fixture.Build();

        await activator.ActivateAsync(
            "pkg-lonely",
            new ResolvedArtefact
            {
                Name = "lonely-unit",
                Kind = ArtefactKind.Unit,
                Content = """
                    unit:
                      name: lonely-unit
                      description: No members at all
                    """,
            },
            Guid.NewGuid(),
            symbolMap,
            cancellationToken: TestContext.Current.CancellationToken);

        await fixture.Directory.DidNotReceive()
            .ListAllAsync(Arg.Any<CancellationToken>());
        fixture.CapturedManifest.ShouldNotBeNull();
    }

    /// <summary>
    /// Test fixture: builds a <see cref="DefaultPackageArtefactActivator"/>
    /// wired with substituted dependencies that capture the manifest the
    /// activator forwards to <see cref="IUnitCreationService"/>. The
    /// captured manifest is what the unit-creation service would receive
    /// — comparing it to expectations is how we validate the rewrite.
    /// </summary>
    private sealed class Fixture
    {
        public IUnitCreationService UnitCreation { get; }
        public IDirectoryService Directory { get; }
        public List<DirectoryEntry> DirectoryEntries { get; } = new();
        public UnitManifest? CapturedManifest { get; private set; }
        public UnitCreationOverrides? CapturedOverrides { get; private set; }

        public Fixture()
        {
            UnitCreation = Substitute.For<IUnitCreationService>();
            Directory = Substitute.For<IDirectoryService>();

            Directory.ListAllAsync(Arg.Any<CancellationToken>())
                .Returns(_ => (IReadOnlyList<DirectoryEntry>)DirectoryEntries.ToList());

            UnitCreation.CreateFromManifestAsync(
                    Arg.Any<UnitManifest>(),
                    Arg.Any<UnitCreationOverrides>(),
                    Arg.Any<CancellationToken>(),
                    Arg.Any<UnitConnectorBindingRequest?>())
                .Returns(call =>
                {
                    CapturedManifest = call.Arg<UnitManifest>();
                    CapturedOverrides = call.Arg<UnitCreationOverrides>();
                    return Task.FromResult(new UnitCreationResult(
                        new UnitResponse(
                            Guid.NewGuid(),
                            "n",
                            CapturedManifest!.Name ?? "x",
                            CapturedManifest.Description ?? string.Empty,
                            DateTimeOffset.UtcNow,
                            Cvoya.Spring.Core.Units.UnitStatus.Draft,
                            null, null),
                        Array.Empty<string>(),
                        0));
                });
        }

        public DefaultPackageArtefactActivator Build()
        {
            var services = new ServiceCollection();
            var sp = services.BuildServiceProvider();
            return new DefaultPackageArtefactActivator(
                UnitCreation,
                Directory,
                sp.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<DefaultPackageArtefactActivator>.Instance);
        }
    }
}