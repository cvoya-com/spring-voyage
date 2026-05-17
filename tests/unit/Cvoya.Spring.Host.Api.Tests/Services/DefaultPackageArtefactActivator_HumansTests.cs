// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Core.Artefacts;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Packages;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Dapr.Tenancy;
using Cvoya.Spring.Host.Api.Auth;
using Cvoya.Spring.Host.Api.Models;
using Cvoya.Spring.Host.Api.Services;
using Cvoya.Spring.Manifest;

using global::Dapr.Actors.Client;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for the ADR-0044 install-reader wiring inside
/// <see cref="DefaultPackageArtefactActivator"/>. Verifies that
/// <c>humans:</c> declarations land as <see cref="UnitMembershipHumanEntity"/>
/// rows via the registered <see cref="IPackageHumanResolutionPolicy"/>
/// with set-semantic collapse on the unique index.
/// </summary>
public class DefaultPackageArtefactActivator_HumansTests
{
    private static readonly Guid TenantId = OssTenantIds.Default;

    [Fact]
    public async Task ActivateUnitAsync_HumansDeclared_PersistsMembershipRows()
    {
        var fx = new Fixture();
        fx.PolicyResolvesTo(fx.CallerId);
        var unitId = Guid.NewGuid();
        var manifest = BuildManifest(unitId, "engineering",
            ("owner", new[] { "security" }, new[] { "escalation" }));

        await fx.RunActivateAsync(manifest, unitId);

        var rows = await fx.GetMembershipsAsync(unitId);
        rows.Count.ShouldBe(1);
        rows[0].HumanId.ShouldBe(fx.CallerId);
        rows[0].Role.ShouldBe("owner");
        rows[0].Expertise.ShouldBe(new[] { "security" });
        rows[0].Notifications.ShouldBe(new[] { "escalation" });
        rows[0].TenantId.ShouldBe(TenantId);
    }

    [Fact]
    public async Task ActivateUnitAsync_PolicySkipped_NoRowsWritten()
    {
        var fx = new Fixture();
        fx.PolicySkips();
        var unitId = Guid.NewGuid();
        var manifest = BuildManifest(unitId, "engineering",
            ("owner", Array.Empty<string>(), Array.Empty<string>()));

        await fx.RunActivateAsync(manifest, unitId);

        var rows = await fx.GetMembershipsAsync(unitId);
        rows.ShouldBeEmpty();
    }

    [Fact]
    public async Task ActivateUnitAsync_PolicyRejected_ThrowsPackageHumanResolutionException()
    {
        var fx = new Fixture();
        fx.PolicyRejectsWith("tenant policy refuses package-declared humans");
        var unitId = Guid.NewGuid();
        var manifest = BuildManifest(unitId, "engineering",
            ("owner", Array.Empty<string>(), Array.Empty<string>()));

        var ex = await Should.ThrowAsync<PackageHumanResolutionException>(async () =>
            await fx.RunActivateAsync(manifest, unitId));

        ex.Role.ShouldBe("owner");
        ex.Reason!.ShouldContain("tenant policy refuses");
    }

    [Fact]
    public async Task ActivateUnitAsync_LegacyIdentityField_ParserRejectsWithLegacyHumanIdentityField()
    {
        // Pre-ADR-0044 shape: `identity:` / `permission:`. The parser must
        // refuse the legacy form with a precise migration error before the
        // activator ever runs.
        var yaml = """
            apiVersion: spring.voyage/v1
            kind: Unit
            name: legacy-unit
            description: legacy
            humans:
              - identity: owner
                permission: owner
                notifications: ["escalation"]
            """;

        var ex = Should.Throw<ManifestParseException>(() => ManifestParser.Parse(yaml));
        ex.Message.ShouldContain("LegacyHumanIdentityField");
    }

    [Fact]
    public async Task ActivateUnitAsync_LegacyPermissionField_ParserRejectsWithLegacyHumanPermissionField()
    {
        // identity removed but permission kept — the parser must still
        // detect the legacy permission field.
        var yaml = """
            apiVersion: spring.voyage/v1
            kind: Unit
            name: legacy-unit
            description: legacy
            humans:
              - role: owner
                permission: owner
                notifications: ["escalation"]
            """;

        var ex = Should.Throw<ManifestParseException>(() => ManifestParser.Parse(yaml));
        ex.Message.ShouldContain("LegacyHumanPermissionField");
    }

    [Fact]
    public async Task ActivateUnitAsync_HumansWithDuplicateRoleAndIdentity_CollapsesToOneRow()
    {
        // ADR-0044 § 3 collapse: two `[{role: reviewer}, {role: reviewer}]`
        // declarations under OSSPolicy resolve to the same caller, so the
        // unique index makes the second upsert a no-op.
        var fx = new Fixture();
        fx.PolicyResolvesTo(fx.CallerId);
        var unitId = Guid.NewGuid();
        var manifest = BuildManifest(unitId, "engineering",
            ("reviewer", Array.Empty<string>(), Array.Empty<string>()),
            ("reviewer", Array.Empty<string>(), Array.Empty<string>()));

        await fx.RunActivateAsync(manifest, unitId);

        var rows = await fx.GetMembershipsAsync(unitId);
        rows.Count.ShouldBe(1, "duplicate (human, role) declarations collapse on the unique index");
        rows[0].Role.ShouldBe("reviewer");
        rows[0].HumanId.ShouldBe(fx.CallerId);
    }

    [Fact]
    public async Task ActivateUnitAsync_HostedPolicyResolvesEachSlotToDifferentHuman_PersistsTwoRows()
    {
        // ADR-0044 § 3 multi-user: a hosted policy that maps two reviewer
        // declarations to two distinct tenant members produces two rows.
        // The unique index passes because human_id differs.
        var alice = Guid.Parse("00000000-aaaa-aaaa-aaaa-000000000001");
        var bob = Guid.Parse("00000000-bbbb-bbbb-bbbb-000000000001");
        var fx = new Fixture();
        fx.PolicyResolvesPerCall(new[] { new[] { alice }, new[] { bob } });
        var unitId = Guid.NewGuid();
        var manifest = BuildManifest(unitId, "engineering",
            ("reviewer", Array.Empty<string>(), Array.Empty<string>()),
            ("reviewer", Array.Empty<string>(), Array.Empty<string>()));

        await fx.RunActivateAsync(manifest, unitId);

        var rows = await fx.GetMembershipsAsync(unitId);
        rows.Count.ShouldBe(2);
        rows.All(r => r.Role == "reviewer").ShouldBeTrue();
        rows.Select(r => r.HumanId).ShouldBe(new[] { alice, bob }, ignoreOrder: true);
    }

    // ── manifest helper ──────────────────────────────────────────────────

    private static UnitManifest BuildManifest(
        Guid unitId,
        string name,
        params (string Role, string[] Expertise, string[] Notifications)[] humans)
    {
        return new UnitManifest
        {
            ApiVersion = "spring.voyage/v1",
            Kind = "Unit",
            Name = name,
            DisplayName = name,
            Description = name + " description",
            Humans = humans
                .Select(h => new HumanManifest
                {
                    Role = h.Role,
                    Expertise = h.Expertise.ToList(),
                    Notifications = h.Notifications.ToList(),
                })
                .ToList(),
        };
    }

    // ── fixture ──────────────────────────────────────────────────────────

    private sealed class Fixture
    {
        public Guid CallerId { get; } = Guid.Parse("cccccccc-3333-3333-3333-000000000001");

        private readonly IUnitCreationService _unitCreationService = Substitute.For<IUnitCreationService>();
        private readonly IDirectoryService _directoryService = Substitute.For<IDirectoryService>();
        private readonly IActorProxyFactory _actorProxyFactory = Substitute.For<IActorProxyFactory>();
        private readonly IAuthenticatedCallerAccessor _callerAccessor = Substitute.For<IAuthenticatedCallerAccessor>();
        private readonly ITenantContext _tenantContext = Substitute.For<ITenantContext>();
        private readonly StubResolutionPolicy _policy = new();
        private readonly string _dbName = "humans-test-" + Guid.NewGuid().ToString("N");

        public Fixture()
        {
            _tenantContext.CurrentTenantId.Returns(TenantId);
            _callerAccessor
                .GetCallerAddressAsync(Arg.Any<CancellationToken>())
                .Returns(Address.ForIdentity(Address.HumanScheme, CallerId));

            _unitCreationService
                .CreateFromManifestAsync(
                    Arg.Any<UnitManifest>(),
                    Arg.Any<UnitCreationOverrides>(),
                    Arg.Any<CancellationToken>(),
                    Arg.Any<UnitConnectorBindingRequest?>())
                .Returns(call =>
                {
                    var name = call.Arg<UnitManifest>().Name ?? "test";
                    var display = call.Arg<UnitManifest>().DisplayName ?? name;
                    var desc = call.Arg<UnitManifest>().Description ?? string.Empty;
                    return Task.FromResult(new UnitCreationResult(
                        Unit: new UnitResponse(
                            Id: Guid.NewGuid(),
                            Name: name,
                            DisplayName: display,
                            Description: desc,
                            RegisteredAt: DateTimeOffset.UtcNow,
                            Status: Cvoya.Spring.Core.Lifecycle.LifecycleStatus.Draft,
                            Model: null,
                            Color: null),
                        Warnings: Array.Empty<string>(),
                        MembersAdded: 0));
                });
        }

        public void PolicyResolvesTo(Guid humanId)
        {
            _policy.Behaviour = _ => new PackageHumanResolution(
                PackageHumanResolutionOutcome.Resolved, new[] { humanId });
        }

        public void PolicyResolvesPerCall(IReadOnlyList<IReadOnlyList<Guid>> sequence)
        {
            var i = 0;
            _policy.Behaviour = _ =>
            {
                var slot = sequence[i++];
                return new PackageHumanResolution(
                    PackageHumanResolutionOutcome.Resolved, slot);
            };
        }

        public void PolicySkips()
        {
            _policy.Behaviour = _ => new PackageHumanResolution(
                PackageHumanResolutionOutcome.Skipped, Array.Empty<Guid>(),
                Reason: "test-skipped");
        }

        public void PolicyRejectsWith(string reason)
        {
            _policy.Behaviour = _ => new PackageHumanResolution(
                PackageHumanResolutionOutcome.Rejected, Array.Empty<Guid>(),
                Reason: reason);
        }

        public async Task RunActivateAsync(UnitManifest manifest, Guid unitId)
        {
            var services = new ServiceCollection();
            services.AddDbContext<SpringDbContext>(opt => opt
                .UseInMemoryDatabase(_dbName)
                .ConfigureWarnings(w => w.Ignore(
                    Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning)));
            services.AddSingleton<ITenantContext>(_ => _tenantContext);
            var sp = services.BuildServiceProvider();
            var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

            var activator = new DefaultPackageArtefactActivator(
                _unitCreationService,
                _directoryService,
                scopeFactory,
                _actorProxyFactory,
                _policy,
                _callerAccessor,
                _tenantContext,
                NullLogger<DefaultPackageArtefactActivator>.Instance);

            // Pre-seed the local-symbol map with the unit's Guid so the
            // activator's symbolMap.GetOrMint returns the known unitId
            // (the test wants to assert against this specific Guid).
            var symbolMap = new LocalSymbolMap();
            symbolMap.Bind(ArtefactKind.Unit, manifest.Name!, unitId);

            var artefact = new ResolvedArtefact
            {
                Kind = ArtefactKind.Unit,
                Name = manifest.Name!,
                Content = SerializeManifest(manifest),
            };

            await activator.ActivateAsync(
                packageName: "test-package",
                artefact: artefact,
                installId: Guid.NewGuid(),
                symbolMap: symbolMap,
                cancellationToken: TestContext.Current.CancellationToken);
        }

        public async Task<IReadOnlyList<UnitMembershipHumanEntity>> GetMembershipsAsync(Guid unitId)
        {
            var services = new ServiceCollection();
            services.AddDbContext<SpringDbContext>(opt => opt
                .UseInMemoryDatabase(_dbName)
                .ConfigureWarnings(w => w.Ignore(
                    Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning)));
            services.AddSingleton<ITenantContext>(_ => _tenantContext);
            var sp = services.BuildServiceProvider();
            await using var scope = sp.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
            return await db.UnitMembershipsHumans
                .Where(m => m.UnitId == unitId)
                .ToListAsync();
        }

        private static string SerializeManifest(UnitManifest manifest)
        {
            // The activator's ManifestParser.Parse runs against YAML, so
            // emit a faithful YAML projection of the test manifest. Use a
            // simple deterministic serializer keeping only the fields the
            // activator's human path actually reads (apiVersion / kind /
            // name / description / humans).
            var humans = manifest.Humans?.Select(h => new
            {
                role = h.Role,
                expertise = h.Expertise,
                notifications = h.Notifications,
            }).ToList();

            var builder = new System.Text.StringBuilder();
            builder.AppendLine($"apiVersion: {manifest.ApiVersion}");
            builder.AppendLine($"kind: {manifest.Kind}");
            builder.AppendLine($"name: {manifest.Name}");
            if (!string.IsNullOrEmpty(manifest.DisplayName))
            {
                builder.AppendLine($"displayName: {manifest.DisplayName}");
            }
            builder.AppendLine($"description: {manifest.Description}");
            if (humans is { Count: > 0 })
            {
                builder.AppendLine("humans:");
                foreach (var h in humans)
                {
                    builder.AppendLine($"  - role: {h.role}");
                    if (h.expertise is { Count: > 0 })
                    {
                        builder.AppendLine($"    expertise: [{string.Join(", ", h.expertise)}]");
                    }
                    if (h.notifications is { Count: > 0 })
                    {
                        builder.AppendLine($"    notifications: [{string.Join(", ", h.notifications)}]");
                    }
                }
            }
            return builder.ToString();
        }

        private sealed class StubResolutionPolicy : IPackageHumanResolutionPolicy
        {
            public Func<PackageHumanResolutionRequest, PackageHumanResolution> Behaviour { get; set; }
                = _ => new PackageHumanResolution(PackageHumanResolutionOutcome.Skipped, Array.Empty<Guid>());

            public Task<PackageHumanResolution> ResolveAsync(
                PackageHumanResolutionRequest request, CancellationToken cancellationToken = default)
                => Task.FromResult(Behaviour(request));
        }
    }
}
