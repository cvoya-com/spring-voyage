// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Observability;

using System.Net;
using System.Net.Http;
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
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging.Abstractions;

using Shouldly;

using Xunit;

/// <summary>
/// Issue #2503 — exercises <see cref="ForwardingOtlpIngestServiceDecorator"/>:
/// decorator emits to the inner service even when forwarding fails;
/// inner-service failure does not mask through forwarding;
/// redaction is applied before the outbound payload leaves the platform;
/// disabled / missing tenant config skips forwarding entirely.
/// </summary>
public class ForwardingOtlpIngestServiceDecoratorTests : IDisposable
{
    private static readonly Guid TenantA = new("dddddddd-2503-0000-0000-000000000001");

    private readonly ServiceProvider _serviceProvider;
    private readonly TimeProvider _timeProvider = TimeProvider.System;
    private readonly RecordingHttpHandler _httpHandler = new();

    public ForwardingOtlpIngestServiceDecoratorTests()
    {
        var dbName = Guid.NewGuid().ToString();
        var root = new InMemoryDatabaseRoot();

        var services = new ServiceCollection();
        services.AddSingleton<ITenantContext>(new StaticTenantContext(TenantA));
        services.AddDbContext<SpringDbContext>(o => o.UseInMemoryDatabase(dbName, root));
        services.AddSingleton(_timeProvider);
        services.AddScoped<ITenantActivitySettings, TenantActivitySettingsService>();

        // Wire the named forwarder HttpClient to a recording handler so
        // assertions can inspect the wire payload without standing up a
        // real HTTP server.
        services.AddHttpClient(ForwardingOtlpIngestServiceDecorator.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => _httpHandler);

        _serviceProvider = services.BuildServiceProvider();
        using var setupScope = _serviceProvider.CreateScope();
        var db = setupScope.ServiceProvider.GetRequiredService<SpringDbContext>();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _serviceProvider.Dispose();

    private async Task ConfigureForwardAsync(string endpoint, bool enabled = true)
    {
        using var scope = _serviceProvider.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<ITenantActivitySettings>();
        await settings.SetAsync(
            TenantA,
            ActivityCaptureLevel.Full,
            retentionDays: 30,
            externalForward: ExternalForwardUpdate.Set(new ExternalOtelForwardConfig(
                Endpoint: endpoint,
                Protocol: "http/json",
                Headers: new Dictionary<string, string> { ["X-Token"] = "secret" },
                Enabled: enabled)),
            cancellationToken: TestContext.Current.CancellationToken);
    }

    private ForwardingOtlpIngestServiceDecorator CreateDecorator(IOtlpIngestService inner)
    {
        return new ForwardingOtlpIngestServiceDecorator(
            inner,
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            _serviceProvider.GetRequiredService<IHttpClientFactory>(),
            _timeProvider,
            NullLogger<ForwardingOtlpIngestServiceDecorator>.Instance);
    }

    private static OtlpEventIngest BuildEvent(string detailsJson = "{}", Guid? tenant = null)
        => new(
            OtlpEventKind.Log,
            new Address(Address.AgentScheme, Guid.NewGuid()),
            tenant ?? TenantA,
            ThreadId: "t",
            MessageId: "m",
            Timestamp: DateTimeOffset.UtcNow,
            Summary: "summary",
            Severity: ActivitySeverity.Info,
            Details: JsonSerializer.SerializeToElement(JsonDocument.Parse(detailsJson).RootElement));

    [Fact]
    public async Task IngestAsync_Disabled_SkipsForwardingAndPersistsLocally()
    {
        // No config persisted -> ExternalForward is null -> Disabled.
        var inner = new RecordingInner();
        var decorator = CreateDecorator(inner);

        await decorator.IngestAsync(new[] { BuildEvent() }, TestContext.Current.CancellationToken);

        inner.Calls.Count.ShouldBe(1);
        _httpHandler.Requests.ShouldBeEmpty();
        decorator.Status[TenantA].Kind.ShouldBe(ForwardStatusKind.Disabled);
    }

    [Fact]
    public async Task IngestAsync_EnabledConfig_ForwardsAfterInnerCall()
    {
        await ConfigureForwardAsync("https://otel.example.com");
        var inner = new RecordingInner();
        var decorator = CreateDecorator(inner);

        await decorator.IngestAsync(new[] { BuildEvent() }, TestContext.Current.CancellationToken);

        inner.Calls.Count.ShouldBe(1);
        _httpHandler.Requests.Count.ShouldBe(1); // log batch -> /v1/logs
        decorator.Status[TenantA].Kind.ShouldBe(ForwardStatusKind.Success);
    }

    [Fact]
    public async Task IngestAsync_ForwardingFailure_InnerStillCalled()
    {
        await ConfigureForwardAsync("https://otel.example.com");
        _httpHandler.NextResponse = new HttpResponseMessage(HttpStatusCode.InternalServerError);
        _httpHandler.NextResponseAfterRetry = new HttpResponseMessage(HttpStatusCode.InternalServerError);

        var inner = new RecordingInner();
        var decorator = CreateDecorator(inner);

        await decorator.IngestAsync(new[] { BuildEvent() }, TestContext.Current.CancellationToken);

        // Inner persisted normally even though forwarder is broken.
        inner.Calls.Count.ShouldBe(1);
        // Two requests — one initial 500 + one retry.
        _httpHandler.Requests.Count.ShouldBe(2);
        decorator.Status[TenantA].Kind.ShouldBe(ForwardStatusKind.Failure);
    }

    [Fact]
    public async Task IngestAsync_InnerThrows_SurfacesWithoutMasking()
    {
        await ConfigureForwardAsync("https://otel.example.com");
        var inner = new ThrowingInner();
        var decorator = CreateDecorator(inner);

        // Inner exception must propagate — the forwarder doesn't mask it.
        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await decorator.IngestAsync(new[] { BuildEvent() }, TestContext.Current.CancellationToken);
        });

        // Forwarder didn't run because inner threw first.
        _httpHandler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task IngestAsync_RedactsBodyBeforeForwarding()
    {
        await ConfigureForwardAsync("https://otel.example.com");
        var inner = new RecordingInner();
        var decorator = CreateDecorator(inner);

        const string sensitive = """{"headers":{"Authorization":"Bearer sk-real-token"}}""";
        await decorator.IngestAsync(new[] { BuildEvent(detailsJson: sensitive) }, TestContext.Current.CancellationToken);

        _httpHandler.Requests.Count.ShouldBe(1);
        var body = _httpHandler.Requests[0].body;
        body.ShouldNotContain("sk-real-token", customMessage: "redaction MUST run before forwarding leaves the platform.");
        body.ShouldContain(ActivityRedactor.RedactedMarker);
    }

    [Fact]
    public async Task IngestAsync_DisabledFlag_SkipsForwardingButPersists()
    {
        await ConfigureForwardAsync("https://otel.example.com", enabled: false);
        var inner = new RecordingInner();
        var decorator = CreateDecorator(inner);

        await decorator.IngestAsync(new[] { BuildEvent() }, TestContext.Current.CancellationToken);

        inner.Calls.Count.ShouldBe(1);
        _httpHandler.Requests.ShouldBeEmpty();
        decorator.Status[TenantA].Kind.ShouldBe(ForwardStatusKind.Disabled);
    }

    private sealed class RecordingInner : IOtlpIngestService
    {
        public readonly List<int> Calls = new();
        public Task<OtlpIngestResult> IngestAsync(
            IReadOnlyList<OtlpEventIngest> events, CancellationToken cancellationToken = default)
        {
            Calls.Add(events.Count);
            return Task.FromResult(new OtlpIngestResult(events.Count, 0, 0, 0));
        }
    }

    private sealed class ThrowingInner : IOtlpIngestService
    {
        public Task<OtlpIngestResult> IngestAsync(
            IReadOnlyList<OtlpEventIngest> events, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("inner broken");
    }

    private sealed class RecordingHttpHandler : HttpMessageHandler
    {
        public readonly List<(string url, string body)> Requests = new();
        public HttpResponseMessage? NextResponse { get; set; }
        public HttpResponseMessage? NextResponseAfterRetry { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.RequestUri?.ToString() ?? string.Empty, body));

            if (Requests.Count == 1 && NextResponse is not null)
            {
                return NextResponse;
            }
            if (Requests.Count == 2 && NextResponseAfterRetry is not null)
            {
                return NextResponseAfterRetry;
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
