// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Connectors;

using System.Text.Json;

using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Connectors;

using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="ConnectorPromptContextResolver"/> — the
/// #2442 platform-side resolver that walks the subject's direct +
/// inherited bindings and gathers each connector's prompt-context
/// fragment for the platform layer of prompt assembly.
/// </summary>
public class ConnectorPromptContextResolverTests
{
    private static readonly Guid ConnectorAId = new("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid ConnectorBId = new("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid Unit1 = new("dddddddd-0000-0000-0000-000000000001");
    private static readonly Guid ParentUnit = new("eeeeeeee-0000-0000-0000-000000000001");

    private readonly IUnitConnectorBindingStore _bindingStore = Substitute.For<IUnitConnectorBindingStore>();
    private readonly IUnitHierarchyResolver _hierarchyResolver = Substitute.For<IUnitHierarchyResolver>();

    [Fact]
    public async Task ResolveAsync_NoContributors_ReturnsEmpty()
    {
        var resolver = BuildResolver(
            connectorTypes: [new FakeConnectorType(ConnectorAId, "connector-a")],
            contributors: []);

        var result = await resolver.ResolveAsync(
            new Address(Address.UnitScheme, Unit1), TestContext.Current.CancellationToken);

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task ResolveAsync_DirectBinding_ReturnsContributorFragment()
    {
        _bindingStore.GetAsync(Unit1, Arg.Any<CancellationToken>())
            .Returns(new UnitConnectorBinding(ConnectorAId, JsonSerializer.SerializeToElement(new { })));
        _hierarchyResolver.GetParentsAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var contributor = new StubPromptContributor(ConnectorAId, "### connector-a hint");
        var resolver = BuildResolver(
            connectorTypes: [new FakeConnectorType(ConnectorAId, "connector-a")],
            contributors: [contributor]);

        var result = await resolver.ResolveAsync(
            new Address(Address.UnitScheme, Unit1), TestContext.Current.CancellationToken);

        result.ShouldBe(new[] { "### connector-a hint" });
        contributor.LastSubject.ShouldNotBeNull();
        contributor.LastSubject!.Id.ShouldBe(Unit1);
        contributor.LastBindingOwnerUnitId.ShouldBe(Unit1);
    }

    [Fact]
    public async Task ResolveAsync_InheritedBindingFromParent_UsesAncestorOwnerUnit()
    {
        _bindingStore.GetAsync(Unit1, Arg.Any<CancellationToken>())
            .Returns((UnitConnectorBinding?)null);
        _bindingStore.GetAsync(ParentUnit, Arg.Any<CancellationToken>())
            .Returns(new UnitConnectorBinding(ConnectorAId, JsonSerializer.SerializeToElement(new { })));
        _hierarchyResolver.GetParentsAsync(
                Arg.Is<Address>(a => a.Id == Unit1), Arg.Any<CancellationToken>())
            .Returns([new Address(Address.UnitScheme, ParentUnit)]);
        _hierarchyResolver.GetParentsAsync(
                Arg.Is<Address>(a => a.Id == ParentUnit), Arg.Any<CancellationToken>())
            .Returns([]);

        var contributor = new StubPromptContributor(ConnectorAId, "### inherited fragment");
        var resolver = BuildResolver(
            connectorTypes: [new FakeConnectorType(ConnectorAId, "connector-a")],
            contributors: [contributor]);

        var result = await resolver.ResolveAsync(
            new Address(Address.UnitScheme, Unit1), TestContext.Current.CancellationToken);

        result.ShouldBe(new[] { "### inherited fragment" });
        // The subject is the leaf unit; the binding owner is the
        // ancestor — mirrors how the runtime-context seam carries the
        // owner identity through.
        contributor.LastSubject!.Id.ShouldBe(Unit1);
        contributor.LastBindingOwnerUnitId.ShouldBe(ParentUnit);
    }

    [Fact]
    public async Task ResolveAsync_ContributorReturnsNull_FragmentSkipped()
    {
        _bindingStore.GetAsync(Unit1, Arg.Any<CancellationToken>())
            .Returns(new UnitConnectorBinding(ConnectorAId, JsonSerializer.SerializeToElement(new { })));
        _hierarchyResolver.GetParentsAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var contributor = new StubPromptContributor(ConnectorAId, fragment: null);
        var resolver = BuildResolver(
            connectorTypes: [new FakeConnectorType(ConnectorAId, "connector-a")],
            contributors: [contributor]);

        var result = await resolver.ResolveAsync(
            new Address(Address.UnitScheme, Unit1), TestContext.Current.CancellationToken);

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task ResolveAsync_MultipleBindings_ConcatenatesInWalkOrder()
    {
        // Unit1 has connector-a; its parent has connector-b. The walker
        // returns one entry per type id — both should produce a fragment.
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

        var aContributor = new StubPromptContributor(ConnectorAId, "### A fragment");
        var bContributor = new StubPromptContributor(ConnectorBId, "### B fragment");
        var resolver = BuildResolver(
            connectorTypes:
            [
                new FakeConnectorType(ConnectorAId, "connector-a"),
                new FakeConnectorType(ConnectorBId, "connector-b"),
            ],
            contributors: [aContributor, bContributor]);

        var result = await resolver.ResolveAsync(
            new Address(Address.UnitScheme, Unit1), TestContext.Current.CancellationToken);

        result.Count.ShouldBe(2);
        result.ShouldContain("### A fragment");
        result.ShouldContain("### B fragment");
    }

    [Fact]
    public async Task ResolveAsync_DirectBindingShadowsInherited()
    {
        // Both the leaf and the ancestor declare the same connector type id;
        // only the leaf's binding should reach the contributor.
        var leafConfig = JsonSerializer.SerializeToElement(new { src = "leaf" });
        var parentConfig = JsonSerializer.SerializeToElement(new { src = "parent" });
        _bindingStore.GetAsync(Unit1, Arg.Any<CancellationToken>())
            .Returns(new UnitConnectorBinding(ConnectorAId, leafConfig));
        _bindingStore.GetAsync(ParentUnit, Arg.Any<CancellationToken>())
            .Returns(new UnitConnectorBinding(ConnectorAId, parentConfig));
        _hierarchyResolver.GetParentsAsync(
                Arg.Is<Address>(a => a.Id == Unit1), Arg.Any<CancellationToken>())
            .Returns([new Address(Address.UnitScheme, ParentUnit)]);
        _hierarchyResolver.GetParentsAsync(
                Arg.Is<Address>(a => a.Id == ParentUnit), Arg.Any<CancellationToken>())
            .Returns([]);

        var contributor = new StubPromptContributor(ConnectorAId, "### leaf fragment");
        var resolver = BuildResolver(
            connectorTypes: [new FakeConnectorType(ConnectorAId, "connector-a")],
            contributors: [contributor]);

        await resolver.ResolveAsync(
            new Address(Address.UnitScheme, Unit1), TestContext.Current.CancellationToken);

        contributor.LastBindingOwnerUnitId.ShouldBe(Unit1);
        contributor.LastConfig.ShouldNotBeNull();
        contributor.LastConfig!.Value.GetProperty("src").GetString().ShouldBe("leaf");
    }

    [Fact]
    public async Task ResolveAsync_ContributorThrows_PropagatesAsSpringException()
    {
        _bindingStore.GetAsync(Unit1, Arg.Any<CancellationToken>())
            .Returns(new UnitConnectorBinding(ConnectorAId, JsonSerializer.SerializeToElement(new { })));
        _hierarchyResolver.GetParentsAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var contributor = new ThrowingPromptContributor(ConnectorAId);
        var resolver = BuildResolver(
            connectorTypes: [new FakeConnectorType(ConnectorAId, "connector-a")],
            contributors: [contributor]);

        var ex = await Should.ThrowAsync<SpringException>(async () =>
            await resolver.ResolveAsync(
                new Address(Address.UnitScheme, Unit1), TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("connector-a");
    }

    [Fact]
    public async Task ResolveAsync_NonAgentNonUnitScheme_ReturnsEmpty()
    {
        var contributor = new StubPromptContributor(ConnectorAId, "anything");
        var resolver = BuildResolver(
            connectorTypes: [new FakeConnectorType(ConnectorAId, "connector-a")],
            contributors: [contributor]);

        var result = await resolver.ResolveAsync(
            new Address(Address.HumanScheme, Unit1), TestContext.Current.CancellationToken);

        result.ShouldBeEmpty();
    }

    /// <summary>
    /// #2743 — A unit acting as its own agent (ADR-0017) has a direct
    /// connector binding ("self-binding"). The subject address uses the
    /// <c>agent://</c> scheme with the unit's id. Units are not in
    /// <c>unit_memberships</c>, so <c>ListByAgentAsync</c> returns empty;
    /// the walker must fall back to the subject id itself to find the
    /// self-binding.
    /// </summary>
    [Fact]
    public async Task ResolveAsync_UnitAsAgentSubject_SelfBinding_ReturnsFragment()
    {
        _bindingStore.GetAsync(Unit1, Arg.Any<CancellationToken>())
            .Returns(new UnitConnectorBinding(ConnectorAId, JsonSerializer.SerializeToElement(new { })));
        _hierarchyResolver.GetParentsAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var membershipRepo = Substitute.For<IUnitMembershipRepository>();
        membershipRepo.ListByAgentAsync(Unit1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<UnitMembership>());

        var contributor = new StubPromptContributor(ConnectorAId, "### unit-as-agent hint");
        var resolver = BuildResolver(
            connectorTypes: [new FakeConnectorType(ConnectorAId, "connector-a")],
            contributors: [contributor],
            membershipRepo: membershipRepo);

        var result = await resolver.ResolveAsync(
            new Address(Address.AgentScheme, Unit1), TestContext.Current.CancellationToken);

        result.ShouldBe(new[] { "### unit-as-agent hint" });
        contributor.LastSubject.ShouldNotBeNull();
        contributor.LastSubject!.Scheme.ShouldBe(Address.AgentScheme);
        contributor.LastSubject.Id.ShouldBe(Unit1);
        contributor.LastBindingOwnerUnitId.ShouldBe(Unit1);
    }

    [Fact]
    public async Task ResolveAsync_BindingForConnectorWithoutContributor_SkippedSilently()
    {
        _bindingStore.GetAsync(Unit1, Arg.Any<CancellationToken>())
            .Returns(new UnitConnectorBinding(ConnectorBId, JsonSerializer.SerializeToElement(new { })));
        _hierarchyResolver.GetParentsAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns([]);

        // Only connector-a has a prompt contributor; the resolved binding
        // is for connector-b. The resolver must skip it without error —
        // a binding for a connector that ships no prompt contributor is
        // a perfectly legal configuration.
        var aContributor = new StubPromptContributor(ConnectorAId, "### A fragment");
        var resolver = BuildResolver(
            connectorTypes:
            [
                new FakeConnectorType(ConnectorAId, "connector-a"),
                new FakeConnectorType(ConnectorBId, "connector-b"),
            ],
            contributors: [aContributor]);

        var result = await resolver.ResolveAsync(
            new Address(Address.UnitScheme, Unit1), TestContext.Current.CancellationToken);

        result.ShouldBeEmpty();
    }

    private ConnectorPromptContextResolver BuildResolver(
        IEnumerable<IConnectorType> connectorTypes,
        IEnumerable<IConnectorPromptContextContributor> contributors,
        IUnitMembershipRepository? membershipRepo = null)
    {
        var services = new ServiceCollection();
        if (membershipRepo is not null)
        {
            services.AddSingleton(membershipRepo);
        }
        var walker = new ConnectorBindingWalker(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            _bindingStore,
            _hierarchyResolver,
            NullLogger<ConnectorBindingWalker>.Instance);
        return new ConnectorPromptContextResolver(
            walker,
            connectorTypes,
            contributors,
            NullLogger<ConnectorPromptContextResolver>.Instance);
    }

    private sealed class StubPromptContributor(Guid connectorTypeId, string? fragment)
        : IConnectorPromptContextContributor
    {
        public Guid ConnectorTypeId { get; } = connectorTypeId;
        public Address? LastSubject { get; private set; }
        public Guid LastBindingOwnerUnitId { get; private set; }
        public JsonElement? LastConfig { get; private set; }

        public Task<string?> GetPromptHintsAsync(
            Address subject,
            Guid bindingOwnerUnitId,
            UnitConnectorBinding binding,
            CancellationToken cancellationToken = default)
        {
            LastSubject = subject;
            LastBindingOwnerUnitId = bindingOwnerUnitId;
            LastConfig = binding.Config;
            return Task.FromResult(fragment);
        }
    }

    private sealed class ThrowingPromptContributor(Guid connectorTypeId)
        : IConnectorPromptContextContributor
    {
        public Guid ConnectorTypeId { get; } = connectorTypeId;

        public Task<string?> GetPromptHintsAsync(
            Address subject,
            Guid bindingOwnerUnitId,
            UnitConnectorBinding binding,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("synthetic prompt contributor failure");
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
