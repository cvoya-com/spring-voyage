// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Skills;

using System.Text.Json;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Core.Tenancy;

using Microsoft.Extensions.Logging;

/// <summary>
/// Platform-level <see cref="ISkillRegistry"/> for the SV runtime
/// reflection tools (issue #2493). Today it exposes a single tool —
/// <c>sv.report_progress</c> — that lets a runtime publish a narrative
/// progress event onto the platform's activity bus from inside MCP
/// rather than via the SDK's OTLP emitter. Both paths produce a
/// <see cref="ActivityEventType.RuntimeProgress"/> event with the same
/// shape; the MCP tool is parity for runtimes that don't use the SDK's
/// helper.
/// </summary>
/// <remarks>
/// <para>
/// The tool's <c>text</c> argument is the human-facing message; the
/// optional <c>kind</c> argument is an event-kind discriminator stamped
/// onto the activity details so consumers can filter (e.g. "starting
/// work" vs "tool call underway").
/// </para>
/// <para>
/// Authz is the same as every other <c>sv.*</c> tool — the caller's
/// identity is resolved from the active MCP session's
/// <see cref="ToolCallContext"/>. The published event's
/// <see cref="ActivityEvent.Source"/> is the caller's address; the
/// tenant scope is the caller's own tenant context.
/// </para>
/// </remarks>
public sealed class SvRuntimeSkillRegistry : ISkillRegistry
{
    /// <summary>Tool name for <c>sv.report_progress</c>.</summary>
    public const string ReportProgressTool = "sv.report_progress";

    private static readonly JsonElement ReportProgressArgSchema = ParseSchema("""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["text"],
          "properties": {
            "text": {
              "type": "string",
              "description": "Human-facing progress message — a single narrative beat (e.g. 'starting work on issue #123', 'tool call underway: github.create_pr', 'PR opened: #4567'). Bounded to a few hundred characters; longer messages are truncated server-side."
            },
            "kind": {
              "type": "string",
              "description": "Optional event-kind discriminator (e.g. 'progress', 'milestone', 'blocker'). Surfaced as an attribute on the published activity event so consumers can filter."
            }
          }
        }
        """);

    private readonly IOtlpIngestService _ingestService;
    private readonly ITenantContext _tenantContext;
    private readonly ILogger _logger;

    private readonly IReadOnlyList<ToolDefinition> _tools;

    public SvRuntimeSkillRegistry(
        IOtlpIngestService ingestService,
        ITenantContext tenantContext,
        ILoggerFactory loggerFactory)
    {
        _ingestService = ingestService;
        _tenantContext = tenantContext;
        _logger = loggerFactory.CreateLogger<SvRuntimeSkillRegistry>();

        _tools = new[]
        {
            new ToolDefinition(
                ReportProgressTool,
                "Emit a narrative progress event for the calling agent or unit. " +
                "Surface in portal/CLI live-tail (#2492) as a RuntimeProgress event. " +
                "Use for meaningful narrative beats during work — starting a step, " +
                "kicking off a tool call, hitting a blocker, finishing. The platform " +
                "rate-limits per-(caller, kind) pair; excess events are dropped silently.",
                ReportProgressArgSchema),
        };
    }

    /// <inheritdoc />
    public string Name => "sv";

    /// <inheritdoc />
    public IReadOnlyList<ToolDefinition> GetToolDefinitions() => _tools;

    /// <inheritdoc />
    public Task<JsonElement> InvokeAsync(string toolName, JsonElement arguments, CancellationToken cancellationToken = default) =>
        throw new SpringException(
            $"Tool '{toolName}' on the {Name} runtime registry requires caller context. " +
            "It is reachable only through the caller-aware ISkillRegistry.InvokeAsync overload " +
            "(invoked by the MCP server with the active session's identity).");

    /// <inheritdoc />
    public async Task<JsonElement> InvokeAsync(
        string toolName,
        JsonElement arguments,
        ToolCallContext context,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(toolName, ReportProgressTool, StringComparison.Ordinal))
        {
            throw new SkillNotFoundException(toolName);
        }

        var text = RequireStringArg(arguments, "text");
        var kind = TryReadStringArg(arguments, "kind");

        if (string.IsNullOrWhiteSpace(context.CallerId)
            || !GuidFormatter.TryParse(context.CallerId, out var callerGuid))
        {
            throw new SpringException(
                $"Tool '{toolName}' requires a caller id; the active MCP session did not supply one.");
        }
        var callerKind = string.IsNullOrWhiteSpace(context.CallerKind)
            ? Address.AgentScheme
            : context.CallerKind;
        var subject = new Address(callerKind, callerGuid);

        // Build an OtlpEventIngest equivalent to the on-wire payload an
        // sv.progress span event would have produced, then hand it to
        // the same ingest service the OTLP plane uses. This means the
        // MCP path and the SDK path produce activity events with the
        // same shape — consumers don't need to special-case which
        // emitter produced an event.
        var details = BuildProgressDetails(text, kind, subject);
        var ingest = new OtlpEventIngest(
            Kind: OtlpEventKind.Progress,
            Subject: subject,
            TenantId: _tenantContext.CurrentTenantId,
            ThreadId: null,
            MessageId: null,
            Timestamp: DateTimeOffset.UtcNow,
            Summary: text.Length > 256 ? text[..256] + "…" : text,
            Severity: ActivitySeverity.Info,
            Details: details);

        try
        {
            await _ingestService.IngestAsync(new[] { ingest }, cancellationToken);
        }
        catch (Exception ex)
        {
            // Best-effort: never raise back to the model. The activity
            // event is a diagnostic signal; failing to record it does
            // not invalidate the work.
            _logger.LogInformation(ex,
                "sv.report_progress: failed to publish RuntimeProgress for caller {Caller}", subject);
        }

        return ParseSchema("""{ "ok": true }""");
    }

    private static JsonElement BuildProgressDetails(string text, string? kind, Address subject)
    {
        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("message", text);
            if (!string.IsNullOrWhiteSpace(kind))
            {
                writer.WriteString("kind", kind);
            }
            writer.WriteString("source", "mcp:sv.report_progress");
            writer.WriteString("sv.subject.uuid", subject.Path);
            writer.WriteString("sv.subject.kind", subject.Scheme);
            writer.WriteEndObject();
        }
        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    private static string RequireStringArg(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object ||
            !args.TryGetProperty(name, out var prop) ||
            prop.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException($"Missing required argument '{name}'.");
        }
        var raw = prop.GetString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new ArgumentException($"Argument '{name}' must be a non-empty string.");
        }
        return raw;
    }

    private static string? TryReadStringArg(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object ||
            !args.TryGetProperty(name, out var prop) ||
            prop.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        var raw = prop.GetString();
        return string.IsNullOrWhiteSpace(raw) ? null : raw;
    }

    private static JsonElement ParseSchema(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
