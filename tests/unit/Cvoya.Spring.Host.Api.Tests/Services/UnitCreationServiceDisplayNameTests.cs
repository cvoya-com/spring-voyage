// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Services;

using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Lifecycle;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Host.Api.Auth;
using Cvoya.Spring.Host.Api.Services;
using Cvoya.Spring.Manifest;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Coverage for the display-name precedence rules implemented in
/// <see cref="UnitCreationService.CreateFromManifestAsync"/>:
/// <list type="number">
///   <item><description>operator-supplied override on
///     <see cref="UnitCreationOverrides.DisplayName"/> wins (used by the
///     install pipeline for the package's single top-level activatable),</description></item>
///   <item><description>manifest's <c>displayName:</c> slot fills the gap,</description></item>
///   <item><description>canonical <c>name:</c> is the final fallback —
///     preserves pre-displayName behaviour for YAMLs that don't declare
///     the field.</description></item>
/// </list>
/// </summary>
public class UnitCreationServiceDisplayNameTests
{

    [Fact]
    public async Task CreateFromManifestAsync_ManifestDisplayName_LandsOnDirectoryEntry()
    {
        // The manifest declares a friendly displayName but the install
        // pipeline supplies NO operator override — the manifest value must
        // win over the canonical `name:` field on the persisted directory
        // entry.
        var fixture = new Fixture();
        var manifest = new UnitManifest
        {
            ApiVersion = "spring.voyage/v1",
            Kind = "Unit",
            Name = "spring-voyage-oss",
            DisplayName = "Spring Voyage OSS",
            Description = "OSS dogfooding org for Spring Voyage.",
        };

        var result = await fixture.Service.CreateFromManifestAsync(
            manifest,
            new UnitCreationOverrides(IsTopLevel: true),
            CancellationToken.None);

        // Service-level response carries the resolved DisplayName.
        result.Unit.DisplayName.ShouldBe("Spring Voyage OSS");
        // Directory write got the same value.
        await fixture.Directory.Received(1).RegisterAsync(
            Arg.Is<DirectoryEntry>(e => e.DisplayName == "Spring Voyage OSS"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateFromManifestAsync_OperatorOverride_WinsOverManifestDisplayName()
    {
        // For the package's top-level activatable, PackageInstallService
        // forwards the operator's `--display-name` flag through
        // UnitCreationOverrides.DisplayName. That value must take
        // precedence over the manifest-declared label — operator intent
        // is the strongest signal.
        var fixture = new Fixture();
        var manifest = new UnitManifest
        {
            ApiVersion = "spring.voyage/v1",
            Kind = "Unit",
            Name = "spring-voyage-oss",
            DisplayName = "Spring Voyage OSS",
            Description = "OSS dogfooding org for Spring Voyage.",
        };

        var result = await fixture.Service.CreateFromManifestAsync(
            manifest,
            new UnitCreationOverrides(
                DisplayName: "My Custom Label",
                IsTopLevel: true),
            CancellationToken.None);

        result.Unit.DisplayName.ShouldBe("My Custom Label");
        await fixture.Directory.Received(1).RegisterAsync(
            Arg.Is<DirectoryEntry>(e => e.DisplayName == "My Custom Label"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateFromManifestAsync_NoDisplayNameAnywhere_FallsBackToManifestName()
    {
        // A YAML declared before the displayName slot existed — neither
        // operator override nor manifest displayName is set. The directory
        // entry must carry the canonical `name:` value (pre-displayName
        // behaviour preserved byte-for-byte for legacy packages).
        var fixture = new Fixture();
        var manifest = new UnitManifest
        {
            ApiVersion = "spring.voyage/v1",
            Kind = "Unit",
            Name = "legacy-unit",
            DisplayName = null,
            Description = "A unit declared before the displayName slot existed.",
        };

        var result = await fixture.Service.CreateFromManifestAsync(
            manifest,
            new UnitCreationOverrides(IsTopLevel: true),
            CancellationToken.None);

        result.Unit.DisplayName.ShouldBe("legacy-unit");
        await fixture.Directory.Received(1).RegisterAsync(
            Arg.Is<DirectoryEntry>(e => e.DisplayName == "legacy-unit"),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Minimal fixture wiring <see cref="UnitCreationService"/> for tests
    /// that only care about the display-name path. Mirrors
    /// <see cref="UnitCreationServiceTests.Fixture"/> with the bits the
    /// override-precedence tests need; copied locally to keep the test
    /// surface independent of the original fixture's evolution.
    /// </summary>
    private sealed class Fixture
    {
        public IDirectoryService Directory { get; } = Substitute.For<IDirectoryService>();
        public IActorProxyFactory ActorProxyFactory { get; } = Substitute.For<IActorProxyFactory>();
        public IAuthenticatedCallerAccessor CallerAccessor { get; } = Substitute.For<IAuthenticatedCallerAccessor>();
        public IUnitConnectorConfigStore ConnectorConfigStore { get; } = Substitute.For<IUnitConnectorConfigStore>();
        public ISkillBundleResolver BundleResolver { get; } = Substitute.For<ISkillBundleResolver>();
        public ISkillBundleValidator BundleValidator { get; } = Substitute.For<ISkillBundleValidator>();
        public IUnitSkillBundleStore BundleStore { get; } = Substitute.For<IUnitSkillBundleStore>();
        public IUnitMemberGraphStore MemberGraphStore { get; } = Substitute.For<IUnitMemberGraphStore>();
        public ITenantContext TenantContext { get; } = Substitute.For<ITenantContext>();
        public IUnitActor Proxy { get; } = Substitute.For<IUnitActor>();
        public UnitCreationService Service { get; }

        public Fixture()
        {
            // Display-name tests run out-of-request; no caller address.
            CallerAccessor.GetCallerAddressAsync(Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<Address?>(null));
            Directory
                .RegisterAsync(Arg.Any<DirectoryEntry>(), Arg.Any<CancellationToken>())
                .Returns(Task.CompletedTask);
            Proxy.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(LifecycleStatus.Draft);
            ActorProxyFactory
                .CreateActorProxy<IUnitActor>(Arg.Any<ActorId>(), Arg.Any<string>())
                .Returns(Proxy);
            ActorProxyFactory
                .CreateActorProxy<IHumanActor>(Arg.Any<ActorId>(), Arg.Any<string>())
                .Returns(Substitute.For<IHumanActor>());

            var services = new ServiceCollection();
            var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

            TenantContext.CurrentTenantId.Returns(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));

            Service = new UnitCreationService(
                Directory,
                ActorProxyFactory,
                CallerAccessor,
                ConnectorConfigStore,
                Array.Empty<IConnectorType>(),
                BundleResolver,
                BundleValidator,
                BundleStore,
                MemberGraphStore,
                TenantContext,
                scopeFactory,
                NullLoggerFactory.Instance);
        }
    }
}
