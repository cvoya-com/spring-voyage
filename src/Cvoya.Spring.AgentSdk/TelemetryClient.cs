// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentSdk;

using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

/// <summary>
/// OTLP/HTTP+JSON implementation of <see cref="ITelemetryClient"/>.
/// Mirrors the Python SDK's <c>TelemetryEmitter</c> + <c>RuntimeContext</c>
/// (issue #2493).
/// </summary>
/// <remarks>
/// <para>
/// Reads OTel env vars set by the launcher
/// (<see cref="OtlpEnvVars"/>) at construction time. A
/// <see cref="TelemetryClient"/> built without an endpoint env var is
/// disabled — every emission method becomes a no-op.
/// </para>
/// <para>
/// Best-effort: any transport failure or rate-limiter trip returns
/// <c>false</c> from the emit methods (or completes the span span
/// silently). The agent's reply path is never blocked by telemetry.
/// </para>
/// </remarks>
public sealed class TelemetryClient : ITelemetryClient, IDisposable
{
    /// <summary>Canonical OTel env-var names producers read.</summary>
    public static class OtlpEnvVars
    {
        public const string Endpoint = "OTEL_EXPORTER_OTLP_ENDPOINT";
        public const string Headers = "OTEL_EXPORTER_OTLP_HEADERS";
        public const string ResourceAttributes = "OTEL_RESOURCE_ATTRIBUTES";
        public const string ServiceName = "OTEL_SERVICE_NAME";
    }

    private const string SvProgressEventName = "sv.progress";
    private const string SvToolCallSpanName = "sv.tool.call";
    private const string SvLlmTurnSpanName = "sv.llm.turn";
    private const string SvAgentTurnSpanName = "sv.agent.turn";
    private const string ResponseDisciplineViolationKind = "response_discipline_violation";

    /// <summary>Stock subject-kind label when the launcher omits resource attributes (defensive).</summary>
    private const string DefaultSubjectKind = "agent";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Uri? _endpoint;
    private readonly IReadOnlyDictionary<string, string> _headers;
    private readonly IReadOnlyDictionary<string, string> _resourceAttributes;
    private readonly string _subjectUuid;
    private readonly ProgressRateLimiter _rateLimiter;
    private readonly Action<string>? _logWarning;

    private readonly string _trace;
    private readonly string _rootSpan;
    private readonly long _rootStartUnixNanos;
    private readonly List<object> _rootEvents = new();
    private readonly object _eventLock = new();

    /// <summary>Constructs a client; prefer <see cref="FromEnvironment(string?, ProgressRateLimiter?, Action{string}?)"/>.</summary>
    public TelemetryClient(
        Uri? endpoint,
        IReadOnlyDictionary<string, string>? headers = null,
        IReadOnlyDictionary<string, string>? resourceAttributes = null,
        HttpClient? httpClient = null,
        ProgressRateLimiter? rateLimiter = null,
        Action<string>? logWarning = null)
    {
        _endpoint = endpoint;
        _headers = headers ?? new Dictionary<string, string>(StringComparer.Ordinal);
        _resourceAttributes = resourceAttributes ?? new Dictionary<string, string>(StringComparer.Ordinal);
        _subjectUuid = _resourceAttributes.TryGetValue("sv.subject.uuid", out var s) ? s : string.Empty;
        _rateLimiter = rateLimiter
            ?? ProgressRateLimiter.FromEnvironment((subject, kind) =>
                logWarning?.Invoke(
                    $"Telemetry rate limit exceeded for subject={subject} kind={kind}; dropping events."));
        _logWarning = logWarning;

        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        _ownsHttp = httpClient is null;

        _trace = RandomHex(16);
        _rootSpan = RandomHex(8);
        _rootStartUnixNanos = NowUnixNanos();
    }

    /// <summary>
    /// Constructs a client from the launcher-injected OTel env vars.
    /// </summary>
    /// <param name="threadId">Optional thread id; stamped on every span.</param>
    /// <param name="rateLimiter">Optional override; falls back to env-driven default.</param>
    /// <param name="logWarning">Optional sink for "rate limit fired" warnings (logs to stderr by default).</param>
    public static TelemetryClient FromEnvironment(
        string? threadId = null,
        ProgressRateLimiter? rateLimiter = null,
        Action<string>? logWarning = null)
    {
        var endpointRaw = Environment.GetEnvironmentVariable(OtlpEnvVars.Endpoint);
        Uri? endpoint = null;
        if (!string.IsNullOrWhiteSpace(endpointRaw)
            && Uri.TryCreate(endpointRaw.TrimEnd('/'), UriKind.Absolute, out var parsed))
        {
            endpoint = parsed;
        }

        var headers = ParseCommaKv(Environment.GetEnvironmentVariable(OtlpEnvVars.Headers));
        var resourceAttrs = new Dictionary<string, string>(
            ParseCommaKv(Environment.GetEnvironmentVariable(OtlpEnvVars.ResourceAttributes)),
            StringComparer.Ordinal);

        var serviceName = Environment.GetEnvironmentVariable(OtlpEnvVars.ServiceName);
        if (!string.IsNullOrWhiteSpace(serviceName) && !resourceAttrs.ContainsKey("service.name"))
        {
            resourceAttrs["service.name"] = serviceName;
        }
        if (!string.IsNullOrEmpty(threadId))
        {
            resourceAttrs["sv.thread.id"] = threadId;
        }
        if (!resourceAttrs.ContainsKey("sv.subject.kind"))
        {
            resourceAttrs["sv.subject.kind"] = DefaultSubjectKind;
        }

        return new TelemetryClient(
            endpoint: endpoint,
            headers: headers,
            resourceAttributes: resourceAttrs,
            rateLimiter: rateLimiter,
            logWarning: logWarning ?? DefaultLogWarning);
    }

    /// <summary>Whether OTLP emission is wired (false when no endpoint env was set).</summary>
    public bool Enabled => _endpoint is not null;

    /// <summary>Resource attributes stamped on every payload — read-only view.</summary>
    public IReadOnlyDictionary<string, string> ResourceAttributes => _resourceAttributes;

    /// <summary>Trace id assigned to the current turn (16 bytes / 32 hex chars).</summary>
    public string TraceId => _trace;

    /// <summary>Root span id assigned to the current turn (8 bytes / 16 hex chars).</summary>
    public string RootSpanId => _rootSpan;

    /// <summary>Subject uuid drawn from the resource attributes.</summary>
    public string SubjectUuid => _subjectUuid;

    /// <inheritdoc />
    public bool ReportProgress(string text, string? kind = null, IReadOnlyDictionary<string, object?>? attributes = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!_rateLimiter.TryAcquire(_subjectUuid, kind ?? "progress"))
        {
            return false;
        }
        return RecordEvent(kind ?? "progress", text, attributes);
    }

    /// <inheritdoc />
    public IToolCallSpan ToolCall(string name, object? arguments = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        var enabled = _rateLimiter.TryAcquire(_subjectUuid, "tool_call");
        return new ToolCallSpanImpl(this, name, arguments, enabled);
    }

    /// <inheritdoc />
    public ILlmTurnSpan LlmTurn(string model, string? prompt = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        var enabled = _rateLimiter.TryAcquire(_subjectUuid, "llm_turn");
        return new LlmTurnSpanImpl(this, model, prompt, enabled);
    }

    /// <summary>
    /// Emit a response-discipline violation event — used by
    /// <see cref="SpringAgent.RunWithResponseDisciplineAsync"/> when the
    /// user delegate exits without calling <c>PostResultAsync</c>.
    /// Bypasses the rate limiter — the violation is a structural event.
    /// </summary>
    public bool EmitResponseDisciplineViolation(string reason)
    {
        return RecordEvent(ResponseDisciplineViolationKind, reason, attributes: null, bypassRateLimit: true);
    }

    /// <summary>Closes the root span — emits the final snapshot.</summary>
    public void Dispose()
    {
        EmitRootSpanSnapshot();
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }

    // -------------------------------------------------------------------------
    // Internals
    // -------------------------------------------------------------------------

    internal bool RecordEvent(string kind, string message, IReadOnlyDictionary<string, object?>? attributes, bool bypassRateLimit = false)
    {
        if (!bypassRateLimit && !_rateLimiter.TryAcquire(_subjectUuid, kind))
        {
            return false;
        }

        var eventObj = BuildSpanEvent(SvProgressEventName, message, kind, attributes);
        lock (_eventLock)
        {
            _rootEvents.Add(eventObj);
        }
        return EmitRootSpanSnapshot();
    }

    internal bool EmitSpan(
        string name,
        string spanId,
        string? parentSpanId,
        long startUnixNanos,
        long endUnixNanos,
        IReadOnlyDictionary<string, object?>? attributes,
        int? statusCode,
        string? statusMessage)
    {
        if (_endpoint is null)
        {
            return false;
        }

        var span = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["traceId"] = _trace,
            ["spanId"] = spanId,
            ["kind"] = 1,
            ["startTimeUnixNano"] = startUnixNanos.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["endTimeUnixNano"] = endUnixNanos.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["attributes"] = BuildKvList(attributes),
        };
        if (parentSpanId is not null)
        {
            span["parentSpanId"] = parentSpanId;
        }
        if (statusCode is not null)
        {
            var status = new Dictionary<string, object?> { ["code"] = statusCode.Value };
            if (!string.IsNullOrEmpty(statusMessage))
            {
                status["message"] = statusMessage;
            }
            span["status"] = status;
        }

        return PostTrace(span);
    }

    private bool EmitRootSpanSnapshot()
    {
        if (_endpoint is null)
        {
            return false;
        }

        List<object> snapshot;
        lock (_eventLock)
        {
            snapshot = new List<object>(_rootEvents);
        }

        var attrs = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (_resourceAttributes.TryGetValue("sv.thread.id", out var tid))
        {
            attrs["sv.thread.id"] = tid;
        }
        var span = new Dictionary<string, object?>
        {
            ["name"] = SvAgentTurnSpanName,
            ["traceId"] = _trace,
            ["spanId"] = _rootSpan,
            ["kind"] = 1,
            ["startTimeUnixNano"] = _rootStartUnixNanos.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["endTimeUnixNano"] = NowUnixNanos().ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["attributes"] = BuildKvList(attrs),
            ["events"] = snapshot,
        };
        return PostTrace(span);
    }

    private bool PostTrace(object span)
    {
        if (_endpoint is null)
        {
            return false;
        }

        var envelope = new
        {
            resourceSpans = new[]
            {
                new
                {
                    resource = new
                    {
                        attributes = BuildKvList(_resourceAttributes.ToDictionary(
                            kv => kv.Key, kv => (object?)kv.Value, StringComparer.Ordinal)),
                    },
                    scopeSpans = new[]
                    {
                        new
                        {
                            scope = new { name = "Cvoya.Spring.AgentSdk", version = "0.1.0" },
                            spans = new[] { span },
                        },
                    },
                },
            },
        };
        return Post("/v1/traces", envelope);
    }

    private bool Post(string path, object envelope)
    {
        if (_endpoint is null)
        {
            return false;
        }
        try
        {
            using var content = new StringContent(
                JsonSerializer.Serialize(envelope, JsonOptions),
                Encoding.UTF8,
                "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_endpoint, path.TrimStart('/')))
            {
                Content = content,
            };
            foreach (var header in _headers)
            {
                if (string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase))
                {
                    if (header.Value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    {
                        request.Headers.Authorization = new AuthenticationHeaderValue(
                            "Bearer", header.Value["Bearer ".Length..]);
                    }
                    else
                    {
                        request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }
                    continue;
                }
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var response = _http.Send(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            // Best-effort: never raise. Transport errors don't break the
            // agent's reply path.
            return false;
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static long NowUnixNanos()
    {
        // DateTimeOffset.ToUnixTimeMilliseconds returns milliseconds
        // since 1970-01-01Z; the OTLP wire wants nanos, so multiply by
        // 1e6. The .UtcTicks-minus-epoch arithmetic overflows long in
        // checked mode, so we go via the millisecond helper.
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L;
    }

    private static string RandomHex(int byteCount)
    {
        Span<byte> buffer = stackalloc byte[byteCount];
        RandomNumberGenerator.Fill(buffer);
        return Convert.ToHexString(buffer).ToLowerInvariant();
    }

    private static Dictionary<string, string> ParseCommaKv(string? raw)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return result;
        }
        foreach (var entry in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = entry.IndexOf('=');
            if (eq <= 0 || eq == entry.Length - 1)
            {
                continue;
            }
            var key = entry[..eq].Trim();
            var value = entry[(eq + 1)..].Trim();
            if (key.Length > 0 && value.Length > 0)
            {
                result[key] = value;
            }
        }
        return result;
    }

    private static object BuildSpanEvent(
        string name, string message, string? kind, IReadOnlyDictionary<string, object?>? attrs)
    {
        var attributes = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["message"] = message,
        };
        if (kind is not null)
        {
            attributes["kind"] = kind;
        }
        if (attrs is not null)
        {
            foreach (var kv in attrs)
            {
                if (!attributes.ContainsKey(kv.Key))
                {
                    attributes[kv.Key] = kv.Value;
                }
            }
        }
        return new
        {
            name,
            timeUnixNano = NowUnixNanos().ToString(System.Globalization.CultureInfo.InvariantCulture),
            attributes = BuildKvList(attributes),
        };
    }

    private static List<object> BuildKvList(IReadOnlyDictionary<string, object?>? attrs)
    {
        var list = new List<object>();
        if (attrs is null)
        {
            return list;
        }
        foreach (var kv in attrs)
        {
            list.Add(new { key = kv.Key, value = BuildAnyValue(kv.Value) });
        }
        return list;
    }

    private static object BuildAnyValue(object? value)
    {
        return value switch
        {
            null => new { stringValue = string.Empty },
            bool b => new Dictionary<string, object?> { ["boolValue"] = b },
            int i => new Dictionary<string, object?> { ["intValue"] = i.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            long l => new Dictionary<string, object?> { ["intValue"] = l.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            double d => new Dictionary<string, object?> { ["doubleValue"] = d },
            float f => new Dictionary<string, object?> { ["doubleValue"] = (double)f },
            string s => new Dictionary<string, object?> { ["stringValue"] = s },
            _ => new Dictionary<string, object?> { ["stringValue"] = value.ToString() ?? string.Empty },
        };
    }

    private static void DefaultLogWarning(string message)
    {
        Console.Error.WriteLine(message);
    }

    // -------------------------------------------------------------------------
    // Span implementations
    // -------------------------------------------------------------------------

    private sealed class ToolCallSpanImpl : IToolCallSpan
    {
        private readonly TelemetryClient _owner;
        private readonly string _name;
        private readonly object? _arguments;
        private readonly bool _enabled;
        private readonly string _spanId;
        private readonly long _startUnixNanos;
        private object? _result;
        private Exception? _error;
        private bool _disposed;

        public ToolCallSpanImpl(TelemetryClient owner, string name, object? arguments, bool enabled)
        {
            _owner = owner;
            _name = name;
            _arguments = arguments;
            _enabled = enabled;
            _spanId = RandomHex(8);
            _startUnixNanos = NowUnixNanos();
        }

        public string TraceId => _owner._trace;
        public string SpanId => _spanId;

        public void SetResult(object? result) => _result = result;

        public void SetError(Exception error) => _error = error;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            if (!_enabled)
            {
                return;
            }

            var attrs = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["tool.name"] = _name,
            };
            if (_arguments is not null)
            {
                var preview = TruncatePreview(_arguments.ToString());
                attrs["tool.args.preview"] = preview;
            }
            if (_result is not null && _error is null)
            {
                attrs["tool.result.preview"] = TruncatePreview(_result.ToString());
            }
            int? statusCode = null;
            string? statusMessage = null;
            if (_error is not null)
            {
                statusCode = 2;
                statusMessage = _error.GetType().Name;
                attrs["exception.type"] = _error.GetType().FullName ?? _error.GetType().Name;
                attrs["exception.message"] = TruncatePreview(_error.Message);
            }

            _owner.EmitSpan(
                name: SvToolCallSpanName,
                spanId: _spanId,
                parentSpanId: _owner._rootSpan,
                startUnixNanos: _startUnixNanos,
                endUnixNanos: NowUnixNanos(),
                attributes: attrs,
                statusCode: statusCode,
                statusMessage: statusMessage);
        }

        private static string TruncatePreview(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }
            return value.Length > 512 ? value[..512] : value;
        }
    }

    private sealed class LlmTurnSpanImpl : ILlmTurnSpan
    {
        private readonly TelemetryClient _owner;
        private readonly string _model;
        private readonly string? _prompt;
        private readonly bool _enabled;
        private readonly string _spanId;
        private readonly long _startUnixNanos;
        private string? _completion;
        private int? _tokensIn;
        private int? _tokensOut;
        private Exception? _error;
        private bool _disposed;

        public LlmTurnSpanImpl(TelemetryClient owner, string model, string? prompt, bool enabled)
        {
            _owner = owner;
            _model = model;
            _prompt = prompt;
            _enabled = enabled;
            _spanId = RandomHex(8);
            _startUnixNanos = NowUnixNanos();
        }

        public string TraceId => _owner._trace;
        public string SpanId => _spanId;

        public void SetCompletion(string? completion, int? tokensInput = null, int? tokensOutput = null)
        {
            _completion = completion;
            _tokensIn = tokensInput;
            _tokensOut = tokensOutput;
        }

        public void SetError(Exception error) => _error = error;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            if (!_enabled)
            {
                return;
            }

            var attrs = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["llm.model"] = _model,
            };
            if (_prompt is not null)
            {
                attrs["llm.prompt.length"] = _prompt.Length;
                attrs["llm.prompt.preview"] = _prompt.Length > 512 ? _prompt[..512] : _prompt;
            }
            if (_completion is not null)
            {
                attrs["llm.completion.length"] = _completion.Length;
                attrs["llm.completion.preview"] = _completion.Length > 512 ? _completion[..512] : _completion;
            }
            if (_tokensIn is not null)
            {
                attrs["llm.tokens.input"] = _tokensIn.Value;
            }
            if (_tokensOut is not null)
            {
                attrs["llm.tokens.output"] = _tokensOut.Value;
            }

            int? statusCode = null;
            string? statusMessage = null;
            if (_error is not null)
            {
                statusCode = 2;
                statusMessage = _error.GetType().Name;
                attrs["exception.type"] = _error.GetType().FullName ?? _error.GetType().Name;
                attrs["exception.message"] = _error.Message.Length > 512 ? _error.Message[..512] : _error.Message;
            }

            _owner.EmitSpan(
                name: SvLlmTurnSpanName,
                spanId: _spanId,
                parentSpanId: _owner._rootSpan,
                startUnixNanos: _startUnixNanos,
                endUnixNanos: NowUnixNanos(),
                attributes: attrs,
                statusCode: statusCode,
                statusMessage: statusMessage);
        }
    }
}
