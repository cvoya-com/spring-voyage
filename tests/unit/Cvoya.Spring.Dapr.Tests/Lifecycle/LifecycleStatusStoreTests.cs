// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Lifecycle;

using Cvoya.Spring.Core.Artefacts;
using Cvoya.Spring.Core.Lifecycle;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Lifecycle;
using Cvoya.Spring.Dapr.Tenancy;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Shouldly;

using Xunit;

/// <summary>
/// Round-trip tests for <see cref="LifecycleStatusStore"/> (#2981): the
/// queryable lifecycle mirror written by the actors and read by the
/// dispatcher cold-start gate, the message-router delivery gate, and the
/// portal status read-path. Exercised against the EF in-memory provider with
/// a static tenant context.
/// </summary>
public class LifecycleStatusStoreTests : IDisposable
{
    private static readonly Guid TenantA = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid AgentA = new("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid UnitA = new("cccccccc-0000-0000-0000-000000000001");

    private readonly string _dbName = Guid.NewGuid().ToString();
    private readonly ServiceProvider _provider;
    private readonly LifecycleStatusStore _store;

    public LifecycleStatusStoreTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITenantContext>(new StaticTenantContext(TenantA));
        // A fixed db name so every per-call scope the store opens shares the
        // same in-memory store (write and read must hit the same database).
        services.AddDbContext<SpringDbContext>(o => o.UseInMemoryDatabase(_dbName));
        _provider = services.BuildServiceProvider();
        _store = new LifecycleStatusStore(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<LifecycleStatusStore>.Instance);
    }

    [Fact]
    public async Task TryGetStatus_returns_null_when_no_row()
    {
        var ct = TestContext.Current.CancellationToken;
        (await _store.TryGetStatusAsync(ArtefactKind.Agent, AgentA, ct)).ShouldBeNull();
        (await _store.TryGetStatusAsync(ArtefactKind.Unit, UnitA, ct)).ShouldBeNull();
    }

    [Theory]
    [InlineData(LifecycleStatus.Running)]
    [InlineData(LifecycleStatus.Stopped)]
    [InlineData(LifecycleStatus.Stopping)]
    [InlineData(LifecycleStatus.Error)]
    public async Task SetStatus_then_TryGetStatus_roundtrips_for_agent(LifecycleStatus status)
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.SetStatusAsync(ArtefactKind.Agent, AgentA, status, ct);
        (await _store.TryGetStatusAsync(ArtefactKind.Agent, AgentA, ct)).ShouldBe(status);
    }

    [Theory]
    [InlineData(LifecycleStatus.Running)]
    [InlineData(LifecycleStatus.Stopped)]
    [InlineData(LifecycleStatus.Stopping)]
    [InlineData(LifecycleStatus.Error)]
    public async Task SetStatus_then_TryGetStatus_roundtrips_for_unit(LifecycleStatus status)
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.SetStatusAsync(ArtefactKind.Unit, UnitA, status, ct);
        (await _store.TryGetStatusAsync(ArtefactKind.Unit, UnitA, ct)).ShouldBe(status);
    }

    [Fact]
    public async Task SetStatus_overwrites_prior_value()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.SetStatusAsync(ArtefactKind.Agent, AgentA, LifecycleStatus.Running, ct);
        await _store.SetStatusAsync(ArtefactKind.Agent, AgentA, LifecycleStatus.Stopped, ct);
        (await _store.TryGetStatusAsync(ArtefactKind.Agent, AgentA, ct)).ShouldBe(LifecycleStatus.Stopped);
    }

    [Fact]
    public async Task SetStatus_is_idempotent_for_unchanged_value()
    {
        // The activation-time sync re-asserts the current status on every cold
        // activation; repeating an unchanged write must leave the value intact.
        var ct = TestContext.Current.CancellationToken;
        await _store.SetStatusAsync(ArtefactKind.Unit, UnitA, LifecycleStatus.Stopped, ct);
        await _store.SetStatusAsync(ArtefactKind.Unit, UnitA, LifecycleStatus.Stopped, ct);
        (await _store.TryGetStatusAsync(ArtefactKind.Unit, UnitA, ct)).ShouldBe(LifecycleStatus.Stopped);
    }

    [Fact]
    public async Task SetStatus_keys_on_kind_so_agent_and_unit_are_independent()
    {
        var ct = TestContext.Current.CancellationToken;
        var sharedId = new Guid("dddddddd-0000-0000-0000-000000000001");
        await _store.SetStatusAsync(ArtefactKind.Agent, sharedId, LifecycleStatus.Running, ct);
        await _store.SetStatusAsync(ArtefactKind.Unit, sharedId, LifecycleStatus.Stopped, ct);

        (await _store.TryGetStatusAsync(ArtefactKind.Agent, sharedId, ct)).ShouldBe(LifecycleStatus.Running);
        (await _store.TryGetStatusAsync(ArtefactKind.Unit, sharedId, ct)).ShouldBe(LifecycleStatus.Stopped);
    }

    public void Dispose()
    {
        _provider.Dispose();
        GC.SuppressFinalize(this);
    }
}
