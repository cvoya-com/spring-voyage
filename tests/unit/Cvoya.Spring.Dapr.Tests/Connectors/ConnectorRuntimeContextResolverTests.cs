// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Connectors;

using System.Text.Json;

using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Connectors;
using Cvoya.Spring.Dapr.Tenancy;

using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="ConnectorRuntimeContextResolver"/> — the #2380
/// seam that walks the subject's direct + inherited connector bindings,
/// invokes each contributor, and merges the env-vars + context files into
/// the dispatch launch spec.
/// </summary>
public class ConnectorRuntimeContextResolverTests
{
    private static readonly Guid ConnectorAId = new("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid ConnectorBId = new("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid Unit1 = new("dddddddd-0000-0000-0000-000000000001");
    private static readonly Guid ParentUnit = new("eeeeeeee-0000-0000-0000-000000000001");
    private static readonly Guid TenantId = OssTenantIds.Default;

    private readonly IUnitConnectorBindingStore _bindingStore = Substitute.For<IUnitConnectorBindingStore>();
    private readonly IUnitHierarchyResolver _hierarchyResolver = Substitute.For<IUnitHierarchyResolver>();
    private readonly ITenantContext _tenantContext = new StaticTenantContext(TenantId);

    [Fact]
    public async Task ResolveAsync_NoContributors_ReturnsEmpty()
    {
        var resolver = BuildResolver(
            connectorTypes: [new FakeConnectorType(ConnectorAId, "connector-a")],
            contributors: []);

        var result = await resolver.ResolveAsync(
            new Address(Address.UnitScheme, Unit1), TestContext.Current.CancellationToken);

        result.ShouldBe(ConnectorRuntimeContextContribution.Empty);
    }

    [Fact]
    public async Task ResolveAsync_UnitSubject_DirectBinding_EmitsContribution()
    {
        _bindingStore.GetAsync(Unit1, Arg.Any<CancellationToken>())
            .Returns(new UnitConnectorBinding(ConnectorAId, JsonSerializer.SerializeToElement(new { })));
        _hierarchyResolver.GetParentsAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var contributor = new RecordingContributor(ConnectorAId, "connector-a",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["SPRING_CONNECTOR_CONNECTOR_A_OWNER"] = "alice",
            },
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["connectors/connector-a/binding.json"] = "{}",
            });

        var resolver = BuildResolver(
            connectorTypes: [new FakeConnectorType(ConnectorAId, "connector-a")],
            contributors: [contributor]);

        var result = await resolver.ResolveAsync(
            new Address(Address.UnitScheme, Unit1), TestContext.Current.CancellationToken);

        result.EnvironmentVariables.ShouldContainKeyAndValue("SPRING_CONNECTOR_CONNECTOR_A_OWNER", "alice");
        result.ContextFiles.ShouldContainKey("connectors/connector-a/binding.json");

        contributor.LastRequest.ShouldNotBeNull();
        contributor.LastRequest!.BindingOwnerUnitId.ShouldBe(Unit1);
        contributor.LastRequest.Subject.Id.ShouldBe(Unit1);
        contributor.LastRequest.TenantId.ShouldBe(TenantId);
    }

    [Fact]
    public async Task ResolveAsync_UnitSubject_InheritedBindingFromParent()
    {
        _bindingStore.GetAsync(Unit1, Arg.Any<CancellationToken>())
            .Returns((UnitConnectorBinding?)null);
        _bindingStore.GetAsync(ParentUnit, Arg.Any<CancellationToken>())
            .Returns(new UnitConnectorBinding(ConnectorAId, JsonSerializer.SerializeToElement(new { repo = "parent" })));
        _hierarchyResolver.GetParentsAsync(
                Arg.Is<Address>(a => a.Id == Unit1), Arg.Any<CancellationToken>())
            .Returns([new Address(Address.UnitScheme, ParentUnit)]);
        _hierarchyResolver.GetParentsAsync(
                Arg.Is<Address>(a => a.Id == ParentUnit), Arg.Any<CancellationToken>())
            .Returns([]);

        var contributor = new RecordingContributor(ConnectorAId, "connector-a",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["SPRING_CONNECTOR_CONNECTOR_A_REPO"] = "parent",
            },
            new Dictionary<string, string>(StringComparer.Ordinal));

        var resolver = BuildResolver(
            connectorTypes: [new FakeConnectorType(ConnectorAId, "connector-a")],
            contributors: [contributor]);

        var result = await resolver.ResolveAsync(
            new Address(Address.UnitScheme, Unit1), TestContext.Current.CancellationToken);

        result.EnvironmentVariables.ShouldContainKeyAndValue("SPRING_CONNECTOR_CONNECTOR_A_REPO", "parent");
        // The contributor is invoked with the ancestor unit as the binding owner.
        contributor.LastRequest!.BindingOwnerUnitId.ShouldBe(ParentUnit);
    }

    [Fact]
    public async Task ResolveAsync_DirectBindingWinsOverInherited()
    {
        _bindingStore.GetAsync(Unit1, Arg.Any<CancellationToken>())
            .Returns(new UnitConnectorBinding(ConnectorAId, JsonSerializer.SerializeToElement(new { repo = "direct" })));
        _bindingStore.GetAsync(ParentUnit, Arg.Any<CancellationToken>())
            .Returns(new UnitConnectorBinding(ConnectorAId, JsonSerializer.SerializeToElement(new { repo = "parent" })));
        _hierarchyResolver.GetParentsAsync(
                Arg.Is<Address>(a => a.Id == Unit1), Arg.Any<CancellationToken>())
            .Returns([new Address(Address.UnitScheme, ParentUnit)]);
        _hierarchyResolver.GetParentsAsync(
                Arg.Is<Address>(a => a.Id == ParentUnit), Arg.Any<CancellationToken>())
            .Returns([]);

        var contributor = new RecordingContributor(ConnectorAId, "connector-a",
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal));

        var resolver = BuildResolver(
            connectorTypes: [new FakeConnectorType(ConnectorAId, "connector-a")],
            contributors: [contributor]);

        await resolver.ResolveAsync(
            new Address(Address.UnitScheme, Unit1), TestContext.Current.CancellationToken);

        contributor.LastRequest!.BindingOwnerUnitId.ShouldBe(Unit1);
    }

    [Fact]
    public async Task ResolveAsync_TwoContributorsMerge_DistinctNamespaces()
    {
        _bindingStore.GetAsync(Unit1, Arg.Any<CancellationToken>())
            .Returns(new UnitConnectorBinding(ConnectorAId, JsonSerializer.SerializeToElement(new { })));
        _bindingStore.GetAsync(ParentUnit, Arg.Any<CancellationToken>())
            .Returns(new UnitConnectorBinding(ConnectorBId, JsonSerializer.SerializeToElement(new { })));
        _hierarchyResolver.GetParentsAsync(
                Arg.Is<Address>(a => a.Id == Unit1), Arg.Any<CancellationToken>())
            .Returns([new Address(Address.UnitScheme, ParentUnit)]);
        _hierarchyResolver.GetParentsAsync(
                Arg.Is<Address>(a => a.Id == ParentUnit), Arg.Any<CancellationToken>())
            .Returns([]);

        var contribA = new RecordingContributor(ConnectorAId, "connector-a",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["SPRING_CONNECTOR_CONNECTOR_A_OWNER"] = "alice",
            },
            new Dictionary<string, string>(StringComparer.Ordinal));
        var contribB = new RecordingContributor(ConnectorBId, "connector-b",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["SPRING_CONNECTOR_CONNECTOR_B_OWNER"] = "bob",
            },
            new Dictionary<string, string>(StringComparer.Ordinal));

        var resolver = BuildResolver(
            connectorTypes:
            [
                new FakeConnectorType(ConnectorAId, "connector-a"),
                new FakeConnectorType(ConnectorBId, "connector-b"),
            ],
            contributors: [contribA, contribB]);

        var result = await resolver.ResolveAsync(
            new Address(Address.UnitScheme, Unit1), TestContext.Current.CancellationToken);

        result.EnvironmentVariables.ShouldContainKeyAndValue("SPRING_CONNECTOR_CONNECTOR_A_OWNER", "alice");
        result.EnvironmentVariables.ShouldContainKeyAndValue("SPRING_CONNECTOR_CONNECTOR_B_OWNER", "bob");
    }

    [Fact]
    public async Task ResolveAsync_EnvVarOutsideNamespace_FailsFast()
    {
        _bindingStore.GetAsync(Unit1, Arg.Any<CancellationToken>())
            .Returns(new UnitConnectorBinding(ConnectorAId, JsonSerializer.SerializeToElement(new { })));
        _hierarchyResolver.GetParentsAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var contributor = new RecordingContributor(ConnectorAId, "connector-a",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                // Wrong namespace — would shadow platform bootstrap.
                ["SPRING_TENANT_ID"] = "oops",
            },
            new Dictionary<string, string>(StringComparer.Ordinal));

        var resolver = BuildResolver(
            connectorTypes: [new FakeConnectorType(ConnectorAId, "connector-a")],
            contributors: [contributor]);

        var ex = await Should.ThrowAsync<SpringException>(async () =>
            await resolver.ResolveAsync(
                new Address(Address.UnitScheme, Unit1), TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("SPRING_CONNECTOR_CONNECTOR_A_");
    }

    [Fact]
    public async Task ResolveAsync_FileOutsideSubPath_FailsFast()
    {
        _bindingStore.GetAsync(Unit1, Arg.Any<CancellationToken>())
            .Returns(new UnitConnectorBinding(ConnectorAId, JsonSerializer.SerializeToElement(new { })));
        _hierarchyResolver.GetParentsAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var contributor = new RecordingContributor(ConnectorAId, "connector-a",
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["tenant-config.json"] = "{}",
            });

        var resolver = BuildResolver(
            connectorTypes: [new FakeConnectorType(ConnectorAId, "connector-a")],
            contributors: [contributor]);

        var ex = await Should.ThrowAsync<SpringException>(async () =>
            await resolver.ResolveAsync(
                new Address(Address.UnitScheme, Unit1), TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("connectors/connector-a/");
    }

    [Fact]
    public async Task ResolveAsync_ContributorThrows_FailsLaunch()
    {
        _bindingStore.GetAsync(Unit1, Arg.Any<CancellationToken>())
            .Returns(new UnitConnectorBinding(ConnectorAId, JsonSerializer.SerializeToElement(new { })));
        _hierarchyResolver.GetParentsAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var contributor = new ThrowingContributor(ConnectorAId);
        var resolver = BuildResolver(
            connectorTypes: [new FakeConnectorType(ConnectorAId, "connector-a")],
            contributors: [contributor]);

        var ex = await Should.ThrowAsync<SpringException>(async () =>
            await resolver.ResolveAsync(
                new Address(Address.UnitScheme, Unit1), TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("connector-a");
    }

    [Fact]
    public async Task ResolveAsync_BindingWithoutContributor_IsSkipped()
    {
        _bindingStore.GetAsync(Unit1, Arg.Any<CancellationToken>())
            .Returns(new UnitConnectorBinding(ConnectorBId, JsonSerializer.SerializeToElement(new { })));
        _hierarchyResolver.GetParentsAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns([]);

        // Only connector A has a contributor, but the binding is for B.
        var contributor = new RecordingContributor(ConnectorAId, "connector-a",
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal));

        var resolver = BuildResolver(
            connectorTypes:
            [
                new FakeConnectorType(ConnectorAId, "connector-a"),
                new FakeConnectorType(ConnectorBId, "connector-b"),
            ],
            contributors: [contributor]);

        var result = await resolver.ResolveAsync(
            new Address(Address.UnitScheme, Unit1), TestContext.Current.CancellationToken);

        result.ShouldBe(ConnectorRuntimeContextContribution.Empty);
        contributor.LastRequest.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveAsync_NonAgentNonUnitScheme_ReturnsEmpty()
    {
        var contributor = new RecordingContributor(ConnectorAId, "connector-a",
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal));
        var resolver = BuildResolver(
            connectorTypes: [new FakeConnectorType(ConnectorAId, "connector-a")],
            contributors: [contributor]);

        var result = await resolver.ResolveAsync(
            new Address(Address.HumanScheme, Unit1), TestContext.Current.CancellationToken);

        result.ShouldBe(ConnectorRuntimeContextContribution.Empty);
    }

    [Fact]
    public void BuildEnvPrefix_NormalisesNonAlphanumerics()
    {
        ConnectorRuntimeContextResolver.BuildEnvPrefix("my-connector")
            .ShouldBe("SPRING_CONNECTOR_MY_CONNECTOR_");
        ConnectorRuntimeContextResolver.BuildEnvPrefix("github")
            .ShouldBe("SPRING_CONNECTOR_GITHUB_");
    }

    [Fact]
    public void BuildFilePrefix_LowerCasesSlug()
    {
        ConnectorRuntimeContextResolver.BuildFilePrefix("GitHub")
            .ShouldBe("connectors/github/");
    }

    private ConnectorRuntimeContextResolver BuildResolver(
        IEnumerable<IConnectorType> connectorTypes,
        IEnumerable<IConnectorRuntimeContextContributor> contributors)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_tenantContext);
        return new ConnectorRuntimeContextResolver(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            _bindingStore,
            _hierarchyResolver,
            _tenantContext,
            connectorTypes,
            contributors,
            NullLogger<ConnectorRuntimeContextResolver>.Instance);
    }

    private sealed class RecordingContributor(
        Guid connectorTypeId,
        string slug,
        IReadOnlyDictionary<string, string> envVars,
        IReadOnlyDictionary<string, string> files) : IConnectorRuntimeContextContributor
    {
        // ReSharper disable once UnusedMember.Local — kept for readability.
        internal string Slug => slug;

        public Guid ConnectorTypeId { get; } = connectorTypeId;

        public ConnectorRuntimeContextRequest? LastRequest { get; private set; }

        public Task<ConnectorRuntimeContextContribution> ContributeAsync(
            ConnectorRuntimeContextRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(new ConnectorRuntimeContextContribution(envVars, files));
        }
    }

    private sealed class ThrowingContributor(Guid connectorTypeId) : IConnectorRuntimeContextContributor
    {
        public Guid ConnectorTypeId { get; } = connectorTypeId;

        public Task<ConnectorRuntimeContextContribution> ContributeAsync(
            ConnectorRuntimeContextRequest request,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("synthetic contributor failure");
    }

    private sealed class FakeConnectorType(Guid typeId, string slug) : IConnectorType
    {
        public Guid TypeId => typeId;
        public string Slug => slug;
        public string DisplayName => slug;
        public string Description => $"{slug} connector";
        public Type ConfigType => typeof(object);

        public void MapRoutes(IEndpointRouteBuilder group) { }

        public Task<JsonElement?> GetConfigSchemaAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<JsonElement?>(null);

        public Task OnUnitStartingAsync(string unitId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task OnUnitStoppingAsync(string unitId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
