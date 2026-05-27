// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.SlackForward;

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

/// <summary>
/// Bridges Slack Socket Mode (a WebSocket Slack pushes events / slash
/// commands / interactions over) onto the local Spring Voyage HTTP API.
/// For every envelope received the bridge mints a v0 signature over the
/// replayed body and POSTs to the matching SV endpoint, then forwards the
/// SV response back to Slack as the envelope's ack payload.
/// </summary>
/// <remarks>
/// The protocol shape is documented at
/// <see href="https://api.slack.com/apis/socket-mode"/>. Inbound payload
/// types reach the bridge wrapped in an envelope:
/// <code>
/// { "envelope_id": "...", "type": "events_api" | "slash_commands" | "interactive",
///   "accepts_response_payload": bool,
///   "payload": { ...the message Slack would have HTTPS-POSTed... } }
/// </code>
/// The bridge unwraps <c>payload</c>, converts it back into the shape SV's
/// HTTP endpoints expect (JSON for <c>events</c>, form-urlencoded for
/// <c>commands</c> and <c>interactions</c>), and mints the same v0
/// signature Slack would have signed with — so the SV signature validator
/// accepts the replayed request without any dev-time bypass.
/// </remarks>
internal sealed class SocketModeBridge
{
    private const int InitialReceiveBufferSize = 8 * 1024;
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);

    private readonly HttpClient _http;
    private readonly string _appToken;
    private readonly string _signingSecret;
    private readonly string _target;
    private readonly TextWriter _stdout;
    private readonly TextWriter _stderr;

    public SocketModeBridge(
        HttpClient http,
        string appToken,
        string signingSecret,
        string target,
        TextWriter stdout,
        TextWriter stderr)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _appToken = appToken ?? throw new ArgumentNullException(nameof(appToken));
        _signingSecret = signingSecret ?? throw new ArgumentNullException(nameof(signingSecret));
        _target = (target ?? throw new ArgumentNullException(nameof(target))).TrimEnd('/');
        _stdout = stdout;
        _stderr = stderr;
    }

    /// <summary>
    /// Connects, dispatches envelopes, reconnects on transient failures
    /// until <paramref name="cancellationToken"/> is signalled. Returns
    /// only when cancellation is requested.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _stdout.WriteLine("spring connector slack forward");
        _stdout.WriteLine("==============================");
        _stdout.WriteLine($"  Target: {_target}");
        _stdout.WriteLine("  Stop with Ctrl-C.");
        _stdout.WriteLine();

        var attempt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var wssUrl = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
                _stdout.WriteLine(attempt == 0
                    ? "[forward] connecting to Slack Socket Mode..."
                    : "[forward] reconnecting to Slack Socket Mode...");
                attempt++;

                using var ws = new ClientWebSocket();
                await ws.ConnectAsync(new Uri(wssUrl), cancellationToken).ConfigureAwait(false);
                _stdout.WriteLine("[forward] connected — waiting for events");
                await PumpAsync(ws, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (SlackForwardPermanentException ex)
            {
                _stderr.WriteLine($"[forward] fatal: {ex.Message}");
                Environment.Exit(1);
                return;
            }
            catch (Exception ex)
            {
                _stderr.WriteLine($"[forward] disconnect: {ex.Message}");
            }

            try
            {
                await Task.Delay(ReconnectDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task<string> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, "https://slack.com/api/apps.connections.open");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _appToken);
        // Slack accepts an empty form-encoded body on this endpoint.
        request.Content = new FormUrlEncodedContent(Array.Empty<KeyValuePair<string, string>>());

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (!root.TryGetProperty("ok", out var okProp) || !okProp.GetBoolean())
        {
            var error = root.TryGetProperty("error", out var errProp) ? errProp.GetString() : null;
            var message =
                $"Slack rejected apps.connections.open: {error ?? "unknown_error"}. " +
                "Common causes: app-level token missing the 'connections:write' scope, " +
                "or Socket Mode is disabled on the app (re-run install with --socket-mode).";
            if (IsPermanentAuthError(error))
            {
                // Bail out of the reconnect loop — retrying invalid_auth /
                // missing_scope / token_revoked just floods stdout with the
                // same line. Surface the cause and exit non-zero.
                throw new SlackForwardPermanentException(message);
            }
            throw new InvalidOperationException(message);
        }
        if (!root.TryGetProperty("url", out var urlProp) || urlProp.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException(
                "Slack's apps.connections.open response is missing 'url'.");
        }
        return urlProp.GetString()!;
    }

    /// <summary>Auth-class errors from <c>apps.connections.open</c> that re-trying will not fix.</summary>
    private static bool IsPermanentAuthError(string? error) => error switch
    {
        "invalid_auth" or "not_authed" or "account_inactive"
            or "token_revoked" or "token_expired" or "no_permission"
            or "missing_scope" => true,
        _ => false,
    };

    private async Task PumpAsync(ClientWebSocket ws, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(InitialReceiveBufferSize);
        try
        {
            while (ws.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var message = await ReceiveMessageAsync(ws, buffer, cancellationToken).ConfigureAwait(false);
                if (message is null)
                {
                    // Either close frame or empty payload — bail and let the outer
                    // reconnect loop spin up a fresh connection.
                    return;
                }
                await DispatchAsync(ws, message, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<string?> ReceiveMessageAsync(
        ClientWebSocket ws, byte[] buffer, CancellationToken cancellationToken)
    {
        using var ms = new MemoryStream();
        while (true)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await ws.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            catch (WebSocketException)
            {
                return null;
            }
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }
            ms.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                break;
            }
        }
        if (ms.Length == 0)
        {
            return null;
        }
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    private async Task DispatchAsync(ClientWebSocket ws, string raw, CancellationToken cancellationToken)
    {
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        var type = root.TryGetProperty("type", out var typeProp) && typeProp.ValueKind == JsonValueKind.String
            ? typeProp.GetString()
            : null;

        switch (type)
        {
            case "hello":
                // Initial handshake — nothing to ack.
                return;
            case "disconnect":
                _stdout.WriteLine("[forward] Slack requested a reconnect");
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "reconnect", cancellationToken)
                    .ConfigureAwait(false);
                return;
            case "events_api":
                await DispatchEventAsync(ws, root, cancellationToken).ConfigureAwait(false);
                return;
            case "slash_commands":
                await DispatchSlashCommandAsync(ws, root, cancellationToken).ConfigureAwait(false);
                return;
            case "interactive":
                await DispatchInteractiveAsync(ws, root, cancellationToken).ConfigureAwait(false);
                return;
            default:
                // Unknown / informational types (e.g. "warning"): log and
                // skip — Slack does not require an ack for unknown types.
                _stdout.WriteLine($"[forward] received '{type}' message (ignored)");
                return;
        }
    }

    private async Task DispatchEventAsync(ClientWebSocket ws, JsonElement envelope, CancellationToken cancellationToken)
    {
        var envelopeId = envelope.GetProperty("envelope_id").GetString()!;
        var payload = envelope.GetProperty("payload").GetRawText();

        var (status, body) = await ReplayAsync(
            path: "/api/v1/tenant/connectors/slack/events",
            body: payload,
            contentType: "application/json",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        LogReplay("events_api", payload, status);

        await SendAckAsync(ws, envelopeId, ResponsePayloadOrNull(envelope, body), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task DispatchSlashCommandAsync(ClientWebSocket ws, JsonElement envelope, CancellationToken cancellationToken)
    {
        var envelopeId = envelope.GetProperty("envelope_id").GetString()!;
        var payload = envelope.GetProperty("payload");
        // Slack's HTTPS slash-command delivery is form-urlencoded; the
        // SV endpoint parses the raw body as form data. Translate the JSON
        // payload back to that shape so the connector sees the same body
        // it would receive over an HTTPS install.
        var form = JsonObjectToForm(payload);
        var (status, body) = await ReplayAsync(
            path: "/api/v1/tenant/connectors/slack/commands",
            body: form,
            contentType: "application/x-www-form-urlencoded",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var label = payload.TryGetProperty("command", out var cmd)
            ? $"slash_command {cmd.GetString()}"
            : "slash_command";
        LogReplay(label, form, status);

        await SendAckAsync(ws, envelopeId, ResponsePayloadOrNull(envelope, body), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task DispatchInteractiveAsync(ClientWebSocket ws, JsonElement envelope, CancellationToken cancellationToken)
    {
        var envelopeId = envelope.GetProperty("envelope_id").GetString()!;
        var payload = envelope.GetProperty("payload").GetRawText();
        // Slack's HTTPS interactivity delivery wraps the JSON payload as a
        // single `payload` field in a form-urlencoded body.
        var form = "payload=" + HttpUtility.UrlEncode(payload);
        var (status, body) = await ReplayAsync(
            path: "/api/v1/tenant/connectors/slack/interactions",
            body: form,
            contentType: "application/x-www-form-urlencoded",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        LogReplay("interactive", form, status);

        await SendAckAsync(ws, envelopeId, ResponsePayloadOrNull(envelope, body), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<(int Status, string Body)> ReplayAsync(
        string path,
        string body,
        string contentType,
        CancellationToken cancellationToken)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            .ToString(CultureInfo.InvariantCulture);
        var signature = ComputeV0Signature(_signingSecret, timestamp, body);

        var url = _target + path;
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, contentType),
        };
        // StringContent's constructor injects a charset; the SV signature
        // validator hashes the body bytes only, so charset on the header
        // is harmless. The two custom Slack headers carry the v0 envelope.
        request.Headers.Add("X-Slack-Request-Timestamp", timestamp);
        request.Headers.Add("X-Slack-Signature", "v0=" + signature);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ((int)response.StatusCode, responseBody);
    }

    private async Task SendAckAsync(
        ClientWebSocket ws, string envelopeId, string? responsePayloadJson, CancellationToken cancellationToken)
    {
        // Slack accepts {"envelope_id": "..."} for fire-and-forget acks
        // (events) and {"envelope_id": "...", "payload": {...}} when the
        // SV endpoint returned a body to relay back to the user (slash
        // commands, interactions).
        string json;
        if (string.IsNullOrEmpty(responsePayloadJson))
        {
            json = JsonSerializer.Serialize(new { envelope_id = envelopeId });
        }
        else
        {
            // The SV endpoint returned a JSON body — pass it through the
            // ack so Slack relays it to the user (e.g. the ephemeral
            // refusal message in ADR-0061 §5).
            using var doc = JsonDocument.Parse(responsePayloadJson);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteString("envelope_id", envelopeId);
                writer.WritePropertyName("payload");
                doc.RootElement.WriteTo(writer);
                writer.WriteEndObject();
            }
            json = Encoding.UTF8.GetString(stream.ToArray());
        }
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken)
            .ConfigureAwait(false);
    }

    private void LogReplay(string label, string body, int status)
    {
        var preview = body.Length > 80 ? body[..80] + "…" : body;
        _stdout.WriteLine($"[forward] {label} → {status} ({preview})");
    }

    /// <summary>
    /// True if the envelope opts into a response payload AND the SV API
    /// returned a non-empty JSON body — returns the raw JSON, otherwise
    /// returns null.
    /// </summary>
    private static string? ResponsePayloadOrNull(JsonElement envelope, string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }
        if (envelope.TryGetProperty("accepts_response_payload", out var prop)
            && prop.ValueKind == JsonValueKind.False)
        {
            return null;
        }
        try
        {
            using var _ = JsonDocument.Parse(body);
            return body;
        }
        catch (JsonException)
        {
            // SV returned a non-JSON 200 (e.g. empty body) — nothing to relay.
            return null;
        }
    }

    /// <summary>
    /// Mints the v0 signature the SV connector verifies on every inbound
    /// HTTPS delivery: HMAC-SHA256 over <c>v0:&lt;timestamp&gt;:&lt;body&gt;</c>
    /// keyed by the deployment's signing secret. Hex output, lower-case —
    /// matches <c>SlackSignatureValidator</c>.
    /// </summary>
    internal static string ComputeV0Signature(string signingSecret, string timestamp, string body)
    {
        var key = Encoding.UTF8.GetBytes(signingSecret);
        var data = Encoding.UTF8.GetBytes($"v0:{timestamp}:{body}");
        var hash = HMACSHA256.HashData(key, data);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Flattens a Slack slash-command JSON payload into the
    /// <c>application/x-www-form-urlencoded</c> body Slack would have POSTed
    /// over HTTPS. Boolean / numeric fields are stringified the way Slack
    /// renders them in the HTTPS form delivery.
    /// </summary>
    /// <summary>
    /// Raised when Slack returns an auth-class error that no amount of
    /// reconnect retries will recover from.
    /// </summary>
    internal sealed class SlackForwardPermanentException : Exception
    {
        public SlackForwardPermanentException(string message) : base(message) { }
    }

    internal static string JsonObjectToForm(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Slash-command payload is not a JSON object.", nameof(payload));
        }

        var sb = new StringBuilder();
        var first = true;
        foreach (var prop in payload.EnumerateObject())
        {
            string value = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString() ?? string.Empty,
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Null => string.Empty,
                JsonValueKind.Number => prop.Value.GetRawText(),
                // Arrays / nested objects are rare on slash-command bodies
                // but show up on a few advanced fields — pass the raw JSON
                // through. The connector's form parser stores values as
                // strings regardless.
                _ => prop.Value.GetRawText(),
            };
            if (!first)
            {
                sb.Append('&');
            }
            first = false;
            sb.Append(HttpUtility.UrlEncode(prop.Name));
            sb.Append('=');
            sb.Append(HttpUtility.UrlEncode(value));
        }
        return sb.ToString();
    }
}
