// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Observability;

using System.Reactive.Linq;
using System.Text;
using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Observability;
using Cvoya.Spring.Dapr.Tenancy;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Shouldly;

using Xunit;

/// <summary>
/// Integration-style tests for <see cref="OtlpIngestService"/> against
/// an in-memory EF store. Exercises the issue #2492 acceptance criteria:
/// capture-level enforcement, redaction at ingest, drop-on-off, and
/// humans-as-activity-subjects.
/// </summary>
public class OtlpIngestServiceTests : IDisposable
{
    private static readonly Guid TenantA = new("dddddddd-0000-0000-0000-000000000001");

    private readonly ServiceProvider _serviceProvider;
    private readonly ActivityEventBus _bus;
    private readonly TimeProvider _timeProvider = TimeProvider.System;

    public OtlpIngestServiceTests()
    {
        // Per-test-class InMemoryDatabaseRoot so writes from one scope
        // are visible to reads from another (EF Core 10's default named
        // registry can rebuild per-scope under some test sequences).
        var dbName = Guid.NewGuid().ToString();
        var root = new InMemoryDatabaseRoot();

        var services = new ServiceCollection();
        services.AddSingleton<ITenantContext>(new StaticTenantContext(TenantA));
        services.AddDbContext<SpringDbContext>(o => o.UseInMemoryDatabase(dbName, root));
        services.AddSingleton(_timeProvider);
        services.AddScoped<ITenantActivitySettings, TenantActivitySettingsService>();
        _serviceProvider = services.BuildServiceProvider();

        using var setupScope = _serviceProvider.CreateScope();
        var db = setupScope.ServiceProvider.GetRequiredService<SpringDbContext>();
        db.Database.EnsureCreated();

        _bus = new ActivityEventBus();
    }

    public void Dispose()
    {
        _bus.Dispose();
        _serviceProvider.Dispose();
    }

    private OtlpIngestService CreateService()
        => new(_bus, _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            _timeProvider, NullLogger<OtlpIngestService>.Instance);

    private async Task SetCaptureLevelAsync(ActivityCaptureLevel level)
    {
        using var scope = _serviceProvider.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<ITenantActivitySettings>();
        await settings.SetAsync(TenantA, level, null, TestContext.Current.CancellationToken);
    }

    private static OtlpEventIngest BuildEvent(
        OtlpEventKind kind = OtlpEventKind.Span,
        Address? subject = null,
        string? prompt = null)
    {
        var subj = subject ?? new Address(Address.AgentScheme, new Guid("aaaaaaaa-0000-0000-0000-000000000001"));
        var details = prompt is null
            ? JsonSerializer.SerializeToElement(new { name = "sv.agent.invoke" })
            : JsonSerializer.SerializeToElement(new { prompt });
        return new OtlpEventIngest(
            kind, subj, TenantA, ThreadId: "thread-1", MessageId: "msg-1",
            Timestamp: DateTimeOffset.UtcNow,
            Summary: "summary",
            Severity: ActivitySeverity.Info,
            Details: details);
    }

    [Fact]
    public async Task IngestAsync_CaptureFull_PublishesFullPayload()
    {
        await SetCaptureLevelAsync(ActivityCaptureLevel.Full);
        var service = CreateService();
        var longPrompt = new string('x', 4000);

        ActivityEvent? observed = null;
        using var sub = _bus.ActivityStream.Subscribe(e => observed = e);

        var result = await service.IngestAsync(new[] { BuildEvent(prompt: longPrompt) }, TestContext.Current.CancellationToken);

        result.Accepted.ShouldBe(1);
        observed.ShouldNotBeNull();
        observed!.Details!.Value.GetProperty("prompt").GetString().ShouldBe(longPrompt);
    }

    [Fact]
    public async Task IngestAsync_CaptureSummary_TruncatesPayloadServerSide()
    {
        await SetCaptureLevelAsync(ActivityCaptureLevel.Summary);
        var service = CreateService();
        var longPrompt = new string('y', 4000);

        ActivityEvent? observed = null;
        using var sub = _bus.ActivityStream.Subscribe(e => observed = e);

        var result = await service.IngestAsync(new[] { BuildEvent(prompt: longPrompt) }, TestContext.Current.CancellationToken);

        result.Accepted.ShouldBe(1);
        observed.ShouldNotBeNull();
        var promptValue = observed!.Details!.Value.GetProperty("prompt").GetString();
        promptValue.ShouldNotBeNull();
        promptValue.Length.ShouldBeLessThan(longPrompt.Length);
        promptValue.ShouldContain("[…]");
        observed.Details!.Value.GetProperty("truncated").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task IngestAsync_CaptureOff_DropsEvents()
    {
        await SetCaptureLevelAsync(ActivityCaptureLevel.Off);

        // Verify the write actually persisted by reading back in a fresh scope.
        using (var scope = _serviceProvider.CreateScope())
        {
            var settings = scope.ServiceProvider.GetRequiredService<ITenantActivitySettings>();
            var readback = await settings.GetAsync(TenantA, TestContext.Current.CancellationToken);
            readback.Level.ShouldBe(ActivityCaptureLevel.Off,
                customMessage: "Setting did not persist across scopes — test setup is wrong.");
        }

        var service = CreateService();

        ActivityEvent? observed = null;
        using var sub = _bus.ActivityStream.Subscribe(e => observed = e);

        var result = await service.IngestAsync(new[] { BuildEvent(), BuildEvent(), BuildEvent() }, TestContext.Current.CancellationToken);

        result.Accepted.ShouldBe(0);
        result.DroppedCapture.ShouldBe(3);
        observed.ShouldBeNull();
    }

    [Fact]
    public async Task IngestAsync_RedactsCredentialHeadersAtIngest()
    {
        await SetCaptureLevelAsync(ActivityCaptureLevel.Full);
        var service = CreateService();

        var details = JsonSerializer.SerializeToElement(new
        {
            headers = new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer sk-ant-real-token",
                ["X-Trace"] = "ok",
            },
            env = new Dictionary<string, string>
            {
                ["ANTHROPIC_API_KEY"] = "sk-ant-real",
                ["HOME"] = "/root",
            },
        });
        var evt = new OtlpEventIngest(
            OtlpEventKind.Log,
            new Address(Address.AgentScheme, Guid.NewGuid()),
            TenantA, null, null, DateTimeOffset.UtcNow,
            "log", ActivitySeverity.Info, details);

        ActivityEvent? observed = null;
        using var sub = _bus.ActivityStream.Subscribe(e => observed = e);

        await service.IngestAsync(new[] { evt }, TestContext.Current.CancellationToken);

        observed.ShouldNotBeNull();
        var d = observed!.Details!.Value;
        d.GetProperty("headers").GetProperty("Authorization").GetString().ShouldBe(ActivityRedactor.RedactedMarker);
        d.GetProperty("env").GetProperty("ANTHROPIC_API_KEY").GetString().ShouldBe(ActivityRedactor.RedactedMarker);
        d.GetProperty("headers").GetProperty("X-Trace").GetString().ShouldBe("ok");
        d.GetProperty("env").GetProperty("HOME").GetString().ShouldBe("/root");
    }

    [Fact]
    public async Task IngestAsync_HumanSubject_FlowsThroughAsActivitySubject()
    {
        await SetCaptureLevelAsync(ActivityCaptureLevel.Full);
        var service = CreateService();
        var humanAddress = new Address(Address.HumanScheme, Guid.NewGuid());

        ActivityEvent? observed = null;
        using var sub = _bus.ActivityStream.Subscribe(e => observed = e);

        await service.IngestAsync(new[] { BuildEvent(subject: humanAddress) }, TestContext.Current.CancellationToken);

        observed.ShouldNotBeNull();
        observed!.Source.Scheme.ShouldBe(Address.HumanScheme);
        observed.Source.Id.ShouldBe(humanAddress.Id);
    }

    [Fact]
    public async Task IngestAsync_BrokenBusPublish_DoesNotThrow()
    {
        // Even when the bus rejects publishes — a stand-in for a real
        // "broken collector path" — the ingest service must surface the
        // failure as a dropped-error count rather than throwing.
        await SetCaptureLevelAsync(ActivityCaptureLevel.Full);
        _bus.Dispose(); // The disposed subject swallows OnNext; ObjectDisposedException would surface on Publish.
        var service = CreateService();

        // Even if all 5 events fail, no exception escapes — the
        // A2A path is unaffected.
        var result = await service.IngestAsync(new[]
        {
            BuildEvent(), BuildEvent(), BuildEvent(),
        }, TestContext.Current.CancellationToken);

        // The bus's Subject.OnNext after Dispose throws ObjectDisposedException,
        // but our internal try/catch must keep the call returning a result.
        // Either all are accepted (no-op publish) or all dropped — either way,
        // the call MUST return cleanly.
        (result.Accepted + result.DroppedError).ShouldBe(3);
    }
}
