// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Observability;

using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Observability;
using Cvoya.Spring.Dapr.Tenancy;

using Google.Protobuf;

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

    private async Task ConfigureForwardAsync(
        string endpoint,
        bool enabled = true,
        string protocol = "http/json")
    {
        using var scope = _serviceProvider.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<ITenantActivitySettings>();
        await settings.SetAsync(
            TenantA,
            ActivityCaptureLevel.Full,
            retentionDays: 30,
            externalForward: ExternalForwardUpdate.Set(new ExternalOtelForwardConfig(
                Endpoint: endpoint,
                Protocol: protocol,
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
        _httpHandler.Requests[0].ContentType.ShouldBe("application/json");
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
        var recorded = _httpHandler.Requests[0];
        recorded.ContentType.ShouldBe("application/json");
        var body = recorded.BodyAsText;
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

    // -----------------------------------------------------------------
    // Issue #2511 — http/protobuf path: every JSON-path test above has a
    // mirrored protobuf-path test below. The forwarder must honour the
    // tenant's configured protocol and emit OTLP LogRecord protobuf
    // bytes with Content-Type: application/x-protobuf.
    // -----------------------------------------------------------------

    [Fact]
    public async Task IngestAsync_EnabledConfig_Protobuf_ForwardsWithProtobufContentType()
    {
        await ConfigureForwardAsync("https://otel.example.com", protocol: "http/protobuf");
        var inner = new RecordingInner();
        var decorator = CreateDecorator(inner);

        await decorator.IngestAsync(new[] { BuildEvent() }, TestContext.Current.CancellationToken);

        inner.Calls.Count.ShouldBe(1);
        _httpHandler.Requests.Count.ShouldBe(1);

        var recorded = _httpHandler.Requests[0];
        recorded.ContentType.ShouldBe("application/x-protobuf");
        recorded.BodyBytes.Length.ShouldBeGreaterThan(0);
        decorator.Status[TenantA].Kind.ShouldBe(ForwardStatusKind.Success);
    }

    [Fact]
    public async Task IngestAsync_Protobuf_BodyRoundTripsToExpectedLogRecord()
    {
        await ConfigureForwardAsync("https://otel.example.com", protocol: "http/protobuf");
        var inner = new RecordingInner();
        var decorator = CreateDecorator(inner);

        var evt = BuildEvent(detailsJson: """{"foo":"bar"}""");
        await decorator.IngestAsync(new[] { evt }, TestContext.Current.CancellationToken);

        var recorded = _httpHandler.Requests.ShouldHaveSingleItem();
        var decoded = TestOtlpLogDecoder.Decode(recorded.BodyBytes);

        // ExportLogsServiceRequest -> ResourceLogs -> Resource attrs.
        decoded.ServiceName.ShouldBe("spring-voyage");
        decoded.TenantId.ShouldBe(TenantA.ToString("D"));

        // ScopeLogs -> InstrumentationScope.
        decoded.ScopeName.ShouldBe("spring-voyage.activity.forward");
        decoded.ScopeVersion.ShouldBe("0.1.0");

        // One LogRecord per event, body + attribute mirror of the JSON shape.
        var record = decoded.LogRecords.ShouldHaveSingleItem();
        record.Body.ShouldBe(evt.Summary);
        record.SeverityText.ShouldBe("INFO");
        record.Attributes["sv.event.kind"].ShouldBe(evt.Kind.ToString());
        record.Attributes["sv.subject"].ShouldBe(evt.Subject.ToString());
        record.Attributes["sv.thread.id"].ShouldBe(evt.ThreadId);
        record.Attributes["sv.message.id"].ShouldBe(evt.MessageId);
        record.Attributes["sv.details"].ShouldContain("\"foo\"");
        record.Attributes["sv.details"].ShouldContain("\"bar\"");

        // trace_id / span_id intentionally absent on the forwarder
        // path — OtlpEventIngest doesn't carry them.
        record.TraceId.ShouldBeNull();
        record.SpanId.ShouldBeNull();
    }

    [Fact]
    public async Task IngestAsync_Protobuf_ForwardingFailure_InnerStillCalled()
    {
        await ConfigureForwardAsync("https://otel.example.com", protocol: "http/protobuf");
        _httpHandler.NextResponse = new HttpResponseMessage(HttpStatusCode.InternalServerError);
        _httpHandler.NextResponseAfterRetry = new HttpResponseMessage(HttpStatusCode.InternalServerError);

        var inner = new RecordingInner();
        var decorator = CreateDecorator(inner);

        await decorator.IngestAsync(new[] { BuildEvent() }, TestContext.Current.CancellationToken);

        inner.Calls.Count.ShouldBe(1);
        _httpHandler.Requests.Count.ShouldBe(2);
        decorator.Status[TenantA].Kind.ShouldBe(ForwardStatusKind.Failure);
    }

    [Fact]
    public async Task IngestAsync_Protobuf_RedactsBodyBeforeForwarding()
    {
        await ConfigureForwardAsync("https://otel.example.com", protocol: "http/protobuf");
        var inner = new RecordingInner();
        var decorator = CreateDecorator(inner);

        const string sensitive = """{"headers":{"Authorization":"Bearer sk-real-token"}}""";
        await decorator.IngestAsync(new[] { BuildEvent(detailsJson: sensitive) }, TestContext.Current.CancellationToken);

        var recorded = _httpHandler.Requests.ShouldHaveSingleItem();
        recorded.ContentType.ShouldBe("application/x-protobuf");

        // Decode and inspect the sv.details attribute — redaction must
        // have run before encoding, so the original token bytes are not
        // anywhere in the wire payload.
        var decoded = TestOtlpLogDecoder.Decode(recorded.BodyBytes);
        var record = decoded.LogRecords.ShouldHaveSingleItem();
        record.Attributes["sv.details"].ShouldNotContain("sk-real-token", customMessage: "redaction MUST run before encoding on the protobuf path.");
        record.Attributes["sv.details"].ShouldContain(ActivityRedactor.RedactedMarker);

        // And the raw wire bytes never contain the token either —
        // belt-and-braces in case any other field accidentally carried
        // the unredacted payload.
        Encoding.UTF8.GetString(recorded.BodyBytes)
            .ShouldNotContain("sk-real-token", customMessage: "no field of the protobuf payload may leak the unredacted token.");
    }

    [Fact]
    public async Task IngestAsync_Protobuf_CaseInsensitiveProtocolMatch()
    {
        // The launcher and SDK both compare protocol case-insensitively
        // (Ordinal/IgnoreCase) — the forwarder must too so an operator
        // who writes "HTTP/PROTOBUF" still gets the protobuf path.
        await ConfigureForwardAsync("https://otel.example.com", protocol: "HTTP/Protobuf");
        var inner = new RecordingInner();
        var decorator = CreateDecorator(inner);

        await decorator.IngestAsync(new[] { BuildEvent() }, TestContext.Current.CancellationToken);

        var recorded = _httpHandler.Requests.ShouldHaveSingleItem();
        recorded.ContentType.ShouldBe("application/x-protobuf");
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

    private sealed class RecordedRequest
    {
        public required string Url { get; init; }
        public required byte[] BodyBytes { get; init; }
        public required string? ContentType { get; init; }

        public string BodyAsText => Encoding.UTF8.GetString(BodyBytes);
    }

    private sealed class RecordingHttpHandler : HttpMessageHandler
    {
        public readonly List<RecordedRequest> Requests = new();
        public HttpResponseMessage? NextResponse { get; set; }
        public HttpResponseMessage? NextResponseAfterRetry { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var bodyBytes = request.Content is null
                ? Array.Empty<byte>()
                : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            Requests.Add(new RecordedRequest
            {
                Url = request.RequestUri?.ToString() ?? string.Empty,
                BodyBytes = bodyBytes,
                ContentType = request.Content?.Headers.ContentType?.MediaType,
            });

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

/// <summary>
/// Minimal OTLP <c>ExportLogsServiceRequest</c> protobuf decoder used by
/// <see cref="ForwardingOtlpIngestServiceDecoratorTests"/> to round-trip
/// the forwarder's protobuf path back into a shape the assertions can
/// pick apart. Mirrors the wire-format reader pattern in
/// <c>Cvoya.Spring.Host.Api.Endpoints.Otlp.OtlpProtobufDecoder</c>; lives
/// here (rather than depending on Host.Api) so the Dapr test project
/// stays independent of the API host's package closure.
/// </summary>
internal static class TestOtlpLogDecoder
{
    public sealed class Decoded
    {
        public string? ServiceName { get; set; }
        public string? TenantId { get; set; }
        public string? ScopeName { get; set; }
        public string? ScopeVersion { get; set; }
        public List<DecodedLogRecord> LogRecords { get; } = new();
    }

    public sealed class DecodedLogRecord
    {
        public string? Body { get; set; }
        public string? SeverityText { get; set; }
        public string? TraceId { get; set; }
        public string? SpanId { get; set; }
        public ulong? TimeUnixNano { get; set; }
        public Dictionary<string, string> Attributes { get; } = new(StringComparer.Ordinal);
    }

    public static Decoded Decode(byte[] payload)
    {
        var result = new Decoded();
        if (payload.Length == 0)
        {
            return result;
        }
        var input = new CodedInputStream(payload);
        while (!input.IsAtEnd)
        {
            var tag = input.ReadTag();
            switch (WireFormat.GetTagFieldNumber(tag))
            {
                case 1: // resource_logs
                    var rlBytes = input.ReadBytes();
                    ReadResourceLogs(new CodedInputStream(rlBytes.ToByteArray()), result);
                    break;
                default:
                    input.SkipLastField();
                    break;
            }
        }
        return result;
    }

    private static void ReadResourceLogs(CodedInputStream input, Decoded result)
    {
        while (!input.IsAtEnd)
        {
            var tag = input.ReadTag();
            switch (WireFormat.GetTagFieldNumber(tag))
            {
                case 1: // resource
                    var resourceBytes = input.ReadBytes();
                    ReadResource(new CodedInputStream(resourceBytes.ToByteArray()), result);
                    break;
                case 2: // scope_logs
                    var scopeBytes = input.ReadBytes();
                    ReadScopeLogs(new CodedInputStream(scopeBytes.ToByteArray()), result);
                    break;
                default:
                    input.SkipLastField();
                    break;
            }
        }
    }

    private static void ReadResource(CodedInputStream input, Decoded result)
    {
        while (!input.IsAtEnd)
        {
            var tag = input.ReadTag();
            switch (WireFormat.GetTagFieldNumber(tag))
            {
                case 1: // attributes (KeyValue)
                    var kvBytes = input.ReadBytes();
                    var (key, value) = ReadKeyValue(new CodedInputStream(kvBytes.ToByteArray()));
                    if (key == "service.name") result.ServiceName = value;
                    else if (key == "sv.tenant.id") result.TenantId = value;
                    break;
                default:
                    input.SkipLastField();
                    break;
            }
        }
    }

    private static void ReadScopeLogs(CodedInputStream input, Decoded result)
    {
        while (!input.IsAtEnd)
        {
            var tag = input.ReadTag();
            switch (WireFormat.GetTagFieldNumber(tag))
            {
                case 1: // scope (InstrumentationScope)
                    var scopeBytes = input.ReadBytes();
                    ReadInstrumentationScope(new CodedInputStream(scopeBytes.ToByteArray()), result);
                    break;
                case 2: // log_records
                    var lrBytes = input.ReadBytes();
                    result.LogRecords.Add(ReadLogRecord(new CodedInputStream(lrBytes.ToByteArray())));
                    break;
                default:
                    input.SkipLastField();
                    break;
            }
        }
    }

    private static void ReadInstrumentationScope(CodedInputStream input, Decoded result)
    {
        while (!input.IsAtEnd)
        {
            var tag = input.ReadTag();
            switch (WireFormat.GetTagFieldNumber(tag))
            {
                case 1: // name
                    result.ScopeName = input.ReadString();
                    break;
                case 2: // version
                    result.ScopeVersion = input.ReadString();
                    break;
                default:
                    input.SkipLastField();
                    break;
            }
        }
    }

    private static DecodedLogRecord ReadLogRecord(CodedInputStream input)
    {
        var record = new DecodedLogRecord();
        while (!input.IsAtEnd)
        {
            var tag = input.ReadTag();
            switch (WireFormat.GetTagFieldNumber(tag))
            {
                case 1: // time_unix_nano (fixed64)
                    record.TimeUnixNano = input.ReadFixed64();
                    break;
                case 3: // severity_text
                    record.SeverityText = input.ReadString();
                    break;
                case 5: // body (AnyValue)
                    var bodyBytes = input.ReadBytes();
                    record.Body = ReadAnyValueAsString(new CodedInputStream(bodyBytes.ToByteArray()));
                    break;
                case 6: // attributes (KeyValue)
                    var attrBytes = input.ReadBytes();
                    var (key, value) = ReadKeyValue(new CodedInputStream(attrBytes.ToByteArray()));
                    record.Attributes[key] = value ?? string.Empty;
                    break;
                case 9: // trace_id (bytes)
                    record.TraceId = Convert.ToHexString(input.ReadBytes().Span).ToLowerInvariant();
                    break;
                case 10: // span_id (bytes)
                    record.SpanId = Convert.ToHexString(input.ReadBytes().Span).ToLowerInvariant();
                    break;
                default:
                    input.SkipLastField();
                    break;
            }
        }
        return record;
    }

    private static (string Key, string? Value) ReadKeyValue(CodedInputStream input)
    {
        string key = string.Empty;
        string? value = null;
        while (!input.IsAtEnd)
        {
            var tag = input.ReadTag();
            switch (WireFormat.GetTagFieldNumber(tag))
            {
                case 1:
                    key = input.ReadString();
                    break;
                case 2:
                    var v = input.ReadBytes();
                    value = ReadAnyValueAsString(new CodedInputStream(v.ToByteArray()));
                    break;
                default:
                    input.SkipLastField();
                    break;
            }
        }
        return (key, value);
    }

    private static string? ReadAnyValueAsString(CodedInputStream input)
    {
        while (!input.IsAtEnd)
        {
            var tag = input.ReadTag();
            switch (WireFormat.GetTagFieldNumber(tag))
            {
                case 1: // string_value
                    return input.ReadString();
                default:
                    input.SkipLastField();
                    break;
            }
        }
        return null;
    }
}
