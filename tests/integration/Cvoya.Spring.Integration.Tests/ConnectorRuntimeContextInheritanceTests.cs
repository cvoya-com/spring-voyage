// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Integration.Tests;

using System.Text.Json;

using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Connectors;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Dapr.Tenancy;

using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Shouldly;

using Xunit;

/// <summary>
/// End-to-end coverage for the connector-runtime-context seam (#2380) against
/// EF-backed binding storage and the production
/// <see cref="Cvoya.Spring.Dapr.Auth.DirectoryUnitHierarchyResolver"/>. The
/// tests stand up an in-memory <see cref="SpringDbContext"/>, write a
/// <c>unit_connector_bindings</c> row on a parent unit, link a child unit
/// through <c>unit_subunit_memberships</c>, and verify the resolver walks
/// the chain and invokes the contributor with the ancestor's binding.
/// </summary>
public class ConnectorRuntimeContextInheritanceTests : IDisposable
{
    private static readonly Guid ConnectorTypeId = new("11111111-aaaa-bbbb-cccc-000000000001");
    private static readonly Guid LeafUnit = new("22222222-aaaa-bbbb-cccc-000000000001");
    private static readonly Guid ParentUnit = new("22222222-aaaa-bbbb-cccc-000000000002");
    private static readonly Guid TenantId = OssTenantIds.Default;
    private static readonly Guid LeafAgent = new("33333333-aaaa-bbbb-cccc-000000000001");

    private readonly ServiceProvider _services;
    private readonly string _dbName = Guid.NewGuid().ToString();

    public ConnectorRuntimeContextInheritanceTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITenantContext>(new StaticTenantContext(TenantId));
        services.AddDbContext<SpringDbContext>(o => o.UseInMemoryDatabase(_dbName));
        services.AddScoped<IUnitConnectorBindingRepository, UnitConnectorBindingRepository>();
        services.AddScoped<IUnitSubunitMembershipRepository, UnitSubunitMembershipRepository>();
        services.AddScoped<IUnitMembershipRepository, UnitMembershipRepository>();
        services.AddSingleton<IUnitConnectorBindingStore, UnitConnectorBindingStore>();
        services.AddSingleton<IUnitHierarchyResolver, Cvoya.Spring.Dapr.Auth.DirectoryUnitHierarchyResolver>();
        services.AddLogging();
        _services = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _services.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<SpringDbContext> CreateDbAsync()
    {
        var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    [Fact]
    public async Task ResolveAsync_LeafUnit_InheritsBindingFromParent_AcrossEf()
    {
        var ct = TestContext.Current.CancellationToken;

        // Wire the parent → child subunit edge in EF.
        await using (var db = await CreateDbAsync())
        {
            db.UnitSubunitMemberships.Add(new UnitSubunitMembershipEntity
            {
                ParentId = ParentUnit,
                ChildId = LeafUnit,
            });
            await db.SaveChangesAsync(ct);
        }

        // Bind the parent unit to a connector type via the binding store.
        var bindingStore = _services.GetRequiredService<IUnitConnectorBindingStore>();
        var configPayload = JsonSerializer.SerializeToElement(new { repo = "parent-repo" });
        await bindingStore.SetAsync(ParentUnit, ConnectorTypeId, configPayload, ct);

        // Recording contributor — captures the invocation so we can assert
        // the resolver carried the ancestor's owner unit through.
        var contributor = new RecordingContributor(ConnectorTypeId, "test-connector");
        var resolver = BuildResolver(bindingStore, contributor);

        var contribution = await resolver.ResolveAsync(
            new Address(Address.UnitScheme, LeafUnit), ct);

        contribution.EnvironmentVariables.ShouldContainKeyAndValue(
            "SPRING_CONNECTOR_TEST_CONNECTOR_OWNER", "parent-owner");
        contribution.ContextFiles.ShouldContainKey(
            ".spring/connectors/test-connector/inherited.json");

        contributor.LastRequest.ShouldNotBeNull();
        contributor.LastRequest!.BindingOwnerUnitId.ShouldBe(ParentUnit);
        contributor.LastRequest.Subject.Id.ShouldBe(LeafUnit);
    }

    [Fact]
    public async Task ResolveAsync_AgentSubject_WalksAgentMembership_ThenParentChain()
    {
        var ct = TestContext.Current.CancellationToken;

        // agent → leaf unit → parent unit; binding lives on the parent.
        await using (var db = await CreateDbAsync())
        {
            db.UnitMemberships.Add(new UnitMembershipEntity
            {
                UnitId = LeafUnit,
                AgentId = LeafAgent,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.UnitSubunitMemberships.Add(new UnitSubunitMembershipEntity
            {
                ParentId = ParentUnit,
                ChildId = LeafUnit,
            });
            await db.SaveChangesAsync(ct);
        }

        var bindingStore = _services.GetRequiredService<IUnitConnectorBindingStore>();
        await bindingStore.SetAsync(
            ParentUnit, ConnectorTypeId,
            JsonSerializer.SerializeToElement(new { }), ct);

        var contributor = new RecordingContributor(ConnectorTypeId, "test-connector");
        var resolver = BuildResolver(bindingStore, contributor);

        var contribution = await resolver.ResolveAsync(
            new Address(Address.AgentScheme, LeafAgent), ct);

        contribution.EnvironmentVariables.ShouldContainKey("SPRING_CONNECTOR_TEST_CONNECTOR_OWNER");
        contributor.LastRequest!.BindingOwnerUnitId.ShouldBe(ParentUnit);
        // The contributor sees the agent as the subject — not the
        // ancestor — so a contributor that wants to scope a credential
        // to the running agent gets the right identity.
        contributor.LastRequest.Subject.Scheme.ShouldBe(Address.AgentScheme);
        contributor.LastRequest.Subject.Id.ShouldBe(LeafAgent);
    }

    [Fact]
    public async Task ResolveAsync_AgentWithoutMembership_ReturnsEmpty()
    {
        var ct = TestContext.Current.CancellationToken;

        // No membership, no binding — but the contributor list is non-empty.
        var contributor = new RecordingContributor(ConnectorTypeId, "test-connector");
        var bindingStore = _services.GetRequiredService<IUnitConnectorBindingStore>();
        var resolver = BuildResolver(bindingStore, contributor);

        var contribution = await resolver.ResolveAsync(
            new Address(Address.AgentScheme, LeafAgent), ct);

        contribution.ShouldBe(ConnectorRuntimeContextContribution.Empty);
        contributor.LastRequest.ShouldBeNull();
    }

    private ConnectorRuntimeContextResolver BuildResolver(
        IUnitConnectorBindingStore bindingStore,
        IConnectorRuntimeContextContributor contributor)
    {
        var walker = new ConnectorBindingWalker(
            _services.GetRequiredService<IServiceScopeFactory>(),
            bindingStore,
            _services.GetRequiredService<IUnitHierarchyResolver>(),
            NullLogger<ConnectorBindingWalker>.Instance);
        return new ConnectorRuntimeContextResolver(
            walker,
            _services.GetRequiredService<ITenantContext>(),
            [new FakeConnectorType(ConnectorTypeId, "test-connector")],
            [contributor],
            NullLogger<ConnectorRuntimeContextResolver>.Instance);
    }

    private sealed class RecordingContributor(Guid connectorTypeId, string slug)
        : IConnectorRuntimeContextContributor
    {
        public Guid ConnectorTypeId { get; } = connectorTypeId;
        public ConnectorRuntimeContextRequest? LastRequest { get; private set; }

        public Task<ConnectorRuntimeContextContribution> ContributeAsync(
            ConnectorRuntimeContextRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            var envKey = $"SPRING_CONNECTOR_{slug.Replace('-', '_').ToUpperInvariant()}_OWNER";
            return Task.FromResult(new ConnectorRuntimeContextContribution(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [envKey] = "parent-owner",
                },
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [$".spring/connectors/{slug}/inherited.json"] = "{\"src\":\"inherited\"}",
                }));
        }
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
