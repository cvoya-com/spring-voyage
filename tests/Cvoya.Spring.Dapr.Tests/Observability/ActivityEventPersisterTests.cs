// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Observability;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Observability;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Shouldly;

using Xunit;

public class ActivityEventPersisterTests : IDisposable
{
    private readonly ActivityEventBus _bus = new();
    private readonly ServiceProvider _serviceProvider;
    private readonly string _dbName = $"PersisterTest-{Guid.NewGuid()}";

    public ActivityEventPersisterTests()
    {
        var services = new ServiceCollection();
        var dbName = _dbName;
        services.AddDbContext<SpringDbContext>(options =>
            options.UseInMemoryDatabase(dbName));
        _serviceProvider = services.BuildServiceProvider();
    }

    private ActivityEventPersister CreatePersister()
    {
        return new ActivityEventPersister(
            _bus,
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ActivityEventPersister>.Instance);
    }

    private static ActivityEvent CreateEvent(string summary = "test")
    {
        return new ActivityEvent(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            Address.For("agent", TestSlugIds.HexFor("test")),
            ActivityEventType.MessageReceived,
            ActivitySeverity.Info,
            summary);
    }

    [Fact]
    public async Task StartAsync_SubscribesToBus()
    {
        var ct = TestContext.Current.CancellationToken;
        var persister = CreatePersister();

        await persister.StartAsync(ct);

        // Publish an event and wait for the 1-second buffer to flush
        _bus.Publish(CreateEvent("persisted-event"));

        // Wait for buffer window (1s) + processing time
        await Task.Delay(3000, ct);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var records = await db.ActivityEvents.ToListAsync(ct);

        records.Count(r => r.Summary == "persisted-event").ShouldBe(1);

        await persister.StopAsync(ct);
        persister.Dispose();
    }

    [Fact]
    public async Task StartAsync_BatchesMultipleEvents()
    {
        var ct = TestContext.Current.CancellationToken;
        var persister = CreatePersister();
        await persister.StartAsync(ct);

        // Publish several events within the 1-second window
        for (var i = 0; i < 5; i++)
        {
            _bus.Publish(CreateEvent($"batch-{i}"));
        }

        await Task.Delay(3000, ct);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var records = await db.ActivityEvents.ToListAsync(ct);

        records.Count().ShouldBe(5);

        await persister.StopAsync(ct);
        persister.Dispose();
    }

    [Fact]
    public async Task StopAsync_StopsProcessing()
    {
        var ct = TestContext.Current.CancellationToken;
        var persister = CreatePersister();
        await persister.StartAsync(ct);
        await persister.StopAsync(ct);

        _bus.Publish(CreateEvent("after-stop"));
        await Task.Delay(2500, ct);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var records = await db.ActivityEvents.ToListAsync(ct);

        records.ShouldBeEmpty();

        persister.Dispose();
    }

    public void Dispose()
    {
        _bus.Dispose();
        _serviceProvider.Dispose();
    }
}