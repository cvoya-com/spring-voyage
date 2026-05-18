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
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Dapr.Tenancy;
using Cvoya.Spring.Dapr.Units;
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
/// Tests for the ADR-0046 install-reader wiring inside
/// <see cref="DefaultPackageArtefactActivator"/>. Verifies that
/// <c>- human:</c> member entries land as <see cref="UnitMembershipHumanEntity"/>
/// rows via the registered <see cref="IPackageHumanResolutionPolicy"/>
/// with set-semantic collapse on the unique <c>(unit, human)</c> index.
/// </summary>
public class DefaultPackageArtefactActivator_HumansTests
{
    private static readonly Guid TenantId = OssTenantIds.Default;

    [Fact]
    public async Task ActivateUnitAsync_HumansDeclared_PersistsMembershipRows()
    {
        var fx = new Fixture();
        var ownerId = Guid.Parse("00000000-aaaa-aaaa-aaaa-000000000001");
        fx.PolicyResolvesTo(ownerId);
        var unitId = Guid.NewGuid();
        var manifest = BuildManifest(unitId, "engineering",
            (new[] { "owner" }, new[] { "security" }, new[] { "escalation" }));

        await fx.RunActivateAsync(manifest, unitId);

        var rows = await fx.GetMembershipsAsync(unitId);
        rows.Count.ShouldBe(1);
        rows[0].HumanId.ShouldBe(ownerId);
        rows[0].Roles.ShouldBe(new[] { "owner" });
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
            (new[] { "owner" }, Array.Empty<string>(), Array.Empty<string>()));

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
            (new[] { "owner" }, Array.Empty<string>(), Array.Empty<string>()));

        var ex = await Should.ThrowAsync<PackageHumanResolutionException>(async () =>
            await fx.RunActivateAsync(manifest, unitId));

        ex.Reason!.ShouldContain("tenant policy refuses");
    }

    [Fact]
    public void ActivateUnitAsync_LegacyHumansBlock_StrictParserRejects()
    {
        // ADR-0046 §1: the legacy top-level `humans:` block is rejected at
        // parse time with the LegacyHumansBlock structured error.
        var yaml = """
            apiVersion: spring.voyage/v1
            kind: Unit
            name: legacy-unit
            description: legacy
            humans:
              - role: owner
                notifications: ["escalation"]
            """;

        var ex = Should.Throw<ManifestParseException>(() => ManifestParser.Parse(yaml));
        ex.Message.ShouldContain("LegacyHumansBlock");
    }

    [Fact]
    public async Task ActivateUnitAsync_TwoEntriesSameRoles_BothResolveDistinctly()
    {
        // ADR-0046 §7: two `- human:` entries with the same roles mint
        // two distinct HumanEntity rows when the policy returns a fresh
        // Guid each call (OSS shape). The unique index keys on
        // (unit, human), so distinct human ids land as two rows.
        var alice = Guid.Parse("00000000-aaaa-aaaa-aaaa-000000000001");
        var bob = Guid.Parse("00000000-bbbb-bbbb-bbbb-000000000001");
        var fx = new Fixture();
        fx.PolicyResolvesPerCall(new[] { new[] { alice }, new[] { bob } });
        var unitId = Guid.NewGuid();
        var manifest = BuildManifest(unitId, "engineering",
            (new[] { "reviewer" }, Array.Empty<string>(), Array.Empty<string>()),
            (new[] { "reviewer" }, Array.Empty<string>(), Array.Empty<string>()));

        await fx.RunActivateAsync(manifest, unitId);

        var rows = await fx.GetMembershipsAsync(unitId);
        rows.Count.ShouldBe(2);
        rows.Select(r => r.HumanId).ShouldBe(new[] { alice, bob }, ignoreOrder: true);
        rows.All(r => r.Roles.SequenceEqual(new[] { "reviewer" })).ShouldBeTrue();
    }

    [Fact]
    public async Task ActivateUnitAsync_CallerAddressIsNull_PolicyReceivesNullInstallCaller()
    {
        // ADR-0046 §10: the install caller is still threaded onto the
        // resolution request so hosted policies that bind by claim can
        // observe it, but the OSS default no longer derives the human id
        // from the caller (it mints a fresh row).
        var fx = new Fixture();
        fx.CallerAccessorReturnsNull();
        Guid? observedInstallCaller = null;
        fx.PolicyCapturesRequest(req => observedInstallCaller = req.InstallCallerHumanId);
        var unitId = Guid.NewGuid();
        var manifest = BuildManifest(unitId, "engineering",
            (new[] { "owner" }, Array.Empty<string>(), Array.Empty<string>()));

        await fx.RunActivateAsync(manifest, unitId);

        observedInstallCaller.ShouldBeNull();
    }

    [Fact]
    public async Task ActivateUnitAsync_RolesAndExpertiseFlowToPolicyRequest()
    {
        // ADR-0046 §3: roles + expertise + notifications are multi-valued
        // and arrive on the policy request as IReadOnlyList<string>.
        var fx = new Fixture();
        fx.PolicyResolvesTo(Guid.NewGuid());
        PackageHumanResolutionRequest? captured = null;
        fx.PolicyCapturesRequest(req => captured = req);
        var unitId = Guid.NewGuid();
        var manifest = BuildManifest(unitId, "engineering",
            (new[] { "owner", "security_lead" }, new[] { "infra", "release" }, new[] { "escalation" }));

        await fx.RunActivateAsync(manifest, unitId);

        captured.ShouldNotBeNull();
        captured!.Roles.ShouldBe(new[] { "owner", "security_lead" });
        captured.Expertise.ShouldBe(new[] { "infra", "release" });
        captured.Notifications.ShouldBe(new[] { "escalation" });
    }

    // ── manifest helper ──────────────────────────────────────────────────

    private static UnitManifest BuildManifest(
        Guid unitId,
        string name,
        params (string[] Roles, string[] Expertise, string[] Notifications)[] humans)
    {
        var members = humans
            .Select(h => new MemberManifest
            {
                Human = InlineArtefactDefinition.FromInline(
                    inlineName: "human",
                    inlineBody: BuildHumanInlineBody(h.Roles, h.Expertise, h.Notifications)),
            })
            .ToList();

        return new UnitManifest
        {
            ApiVersion = "spring.voyage/v1",
            Kind = "Unit",
            Name = name,
            DisplayName = name,
            Description = name + " description",
            Members = members,
        };
    }

    private static string BuildHumanInlineBody(string[] roles, string[] expertise, string[] notifications)
    {
        var sb = new System.Text.StringBuilder();
        if (roles.Length > 0)
        {
            sb.AppendLine($"roles: [{string.Join(", ", roles)}]");
        }
        if (expertise.Length > 0)
        {
            sb.AppendLine($"expertise: [{string.Join(", ", expertise)}]");
        }
        if (notifications.Length > 0)
        {
            sb.AppendLine($"notifications: [{string.Join(", ", notifications)}]");
        }
        if (sb.Length == 0)
        {
            sb.AppendLine("roles: []");
        }
        return sb.ToString();
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

        public void CallerAccessorReturnsNull()
        {
            _callerAccessor
                .GetCallerAddressAsync(Arg.Any<CancellationToken>())
                .Returns((Address?)null);
        }

        public void PolicyCapturesRequest(Action<PackageHumanResolutionRequest> capture)
        {
            var previous = _policy.Behaviour;
            _policy.Behaviour = req =>
            {
                capture(req);
                return previous(req);
            };
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
            services.AddSingleton<IUnitHumanMembershipStore>(sp =>
                new EfUnitHumanMembershipStore(sp.GetRequiredService<IServiceScopeFactory>()));
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

            _scopeFactory = scopeFactory;
        }

        private IServiceScopeFactory? _scopeFactory;

        public async Task<IReadOnlyList<UnitMembershipHumanEntity>> GetMembershipsAsync(Guid unitId)
        {
            // Reuse the same provider the activator wrote against so the
            // assertion reads the rows the test just persisted (in-memory
            // EF stores are per-provider).
            if (_scopeFactory is null)
            {
                return Array.Empty<UnitMembershipHumanEntity>();
            }
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
            return await db.UnitMembershipsHumans
                .Where(m => m.UnitId == unitId)
                .ToListAsync();
        }

        private static string SerializeManifest(UnitManifest manifest)
        {
            // The activator's ManifestParser.Parse runs against YAML, so
            // emit a faithful YAML projection of the test manifest under the
            // ADR-0046 `members: [- human: {...}]` shape.
            var builder = new System.Text.StringBuilder();
            builder.AppendLine($"apiVersion: {manifest.ApiVersion}");
            builder.AppendLine($"kind: {manifest.Kind}");
            builder.AppendLine($"name: {manifest.Name}");
            if (!string.IsNullOrEmpty(manifest.DisplayName))
            {
                builder.AppendLine($"displayName: {manifest.DisplayName}");
            }
            builder.AppendLine($"description: {manifest.Description}");

            if (manifest.Members is { Count: > 0 })
            {
                builder.AppendLine("members:");
                foreach (var m in manifest.Members)
                {
                    if (m.Human?.InlineBody is null) continue;
                    builder.AppendLine("  - human:");
                    foreach (var line in m.Human.InlineBody.Split('\n'))
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        builder.AppendLine($"      {line.TrimEnd()}");
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
