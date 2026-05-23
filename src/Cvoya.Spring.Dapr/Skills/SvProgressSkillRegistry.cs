// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Skills;

using System.Text.Json;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Skills;

using Microsoft.Extensions.Logging;

/// <summary>
/// Platform-level <see cref="ISkillRegistry"/> implementing ADR-0056 §8's
/// fundamental-core progress tool: <c>sv.progress.report(message, fraction?)</c>.
/// Emits a <see cref="ActivityEventType.RuntimeProgress"/> activity for
/// the calling agent or unit so a long-running turn isn't silent until
/// completion.
/// </summary>
/// <remarks>
/// <para>
/// Writes the <see cref="ActivityEventType.RuntimeProgress"/> activity
/// directly via <see cref="IActivityEventBus"/> so the wire shape stays
/// minimal (no OTLP wrapping) and the optional 0..1 <c>fraction</c>
/// argument the ADR calls out is surfaced as a first-class detail field.
/// </para>
/// <para>
/// Authz follows the same pattern as the other <c>sv.*</c> tools — the
/// caller's identity is resolved from the active MCP session's
/// <see cref="ToolCallContext"/>. The published event's
/// <see cref="ActivityEvent.Source"/> is the caller's address; the
/// correlation id is the active thread id.
/// </para>
/// </remarks>
public sealed class SvProgressSkillRegistry : ISkillRegistry
{
    /// <summary>Tool name for <c>sv.progress.report</c>.</summary>
    public const string ReportTool = "sv.progress.report";

    /// <summary>Maximum length of the human-facing message before truncation.</summary>
    public const int MaxMessageLength = 1024;

    private static readonly JsonElement ReportArgSchema = ParseSchema("""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["message"],
          "properties": {
            "message": {
              "type": "string",
              "description": "Human-facing progress beat — a single narrative sentence (e.g. 'reviewing PR diff', 'tests passing — opening PR'). Longer messages are truncated server-side."
            },
            "fraction": {
              "type": "number",
              "minimum": 0,
              "maximum": 1,
              "description": "Optional completion fraction in [0.0, 1.0]. Useful for progress bars; omit when the turn has no meaningful percent-complete signal."
            }
          }
        }
        """);

    private readonly IActivityEventBus _activityEventBus;
    private readonly ILogger _logger;
    private readonly IReadOnlyList<ToolDefinition> _tools;

    public SvProgressSkillRegistry(
        IActivityEventBus activityEventBus,
        ILoggerFactory loggerFactory)
    {
        _activityEventBus = activityEventBus;
        _logger = loggerFactory.CreateLogger<SvProgressSkillRegistry>();
        _tools =
        [
            new ToolDefinition(
                ReportTool,
                "Emit a progress signal for the active turn — a human-facing message and " +
                "an optional [0.0, 1.0] completion fraction. Records a RuntimeProgress " +
                "activity correlated to the current thread so operators see signal before " +
                "the turn completes (ADR-0056 §8 fundamental core). Use this when work is " +
                "non-trivial and otherwise silent — a chatbot replying in one tool call " +
                "doesn't need it.",
                ReportArgSchema,
                ToolCategories.Observability),
        ];
    }

    /// <inheritdoc />
    public string Name => "sv";

    /// <inheritdoc />
    public IReadOnlyList<ToolDefinition> GetToolDefinitions() => _tools;

    /// <inheritdoc />
    public Task<JsonElement> InvokeAsync(
        string toolName, JsonElement arguments, CancellationToken cancellationToken = default) =>
        throw new SpringException(
            $"Tool '{toolName}' on the {Name} progress registry requires caller context. " +
            "It is reachable only through the caller-aware ISkillRegistry.InvokeAsync overload " +
            "(invoked by the MCP server with the active session's identity).");

    /// <inheritdoc />
    public async Task<JsonElement> InvokeAsync(
        string toolName,
        JsonElement arguments,
        ToolCallContext context,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(toolName, ReportTool, StringComparison.Ordinal))
        {
            throw new SkillNotFoundException(toolName);
        }

        var message = RequireStringArg(arguments, "message");
        if (message.Length > MaxMessageLength)
        {
            message = message[..MaxMessageLength] + "…";
        }
        var fraction = TryReadDoubleArg(arguments, "fraction");
        if (fraction is { } f && (f < 0.0 || f > 1.0))
        {
            throw new ArgumentException(
                $"Argument 'fraction' must be between 0.0 and 1.0; got {f}.");
        }

        if (string.IsNullOrWhiteSpace(context.CallerId)
            || !GuidFormatter.TryParse(context.CallerId, out var callerGuid))
        {
            throw new SpringException(
                $"Tool '{ReportTool}' requires a caller id; the active MCP session did not supply one.");
        }
        var callerKind = string.IsNullOrWhiteSpace(context.CallerKind)
            ? Address.AgentScheme
            : context.CallerKind;
        var subject = new Address(callerKind, callerGuid);
        var threadId = GuidFormatter.TryParse(context.ThreadId, out var parsedThreadId)
            ? parsedThreadId
            : Guid.Empty;

        var details = BuildDetails(message, fraction);

        var activityEvent = new ActivityEvent(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            subject,
            ActivityEventType.RuntimeProgress,
            ActivitySeverity.Info,
            message,
            details,
            threadId == Guid.Empty ? null : threadId.ToString("D"));

        try
        {
            await _activityEventBus.PublishAsync(activityEvent, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort: never raise back to the model. Reporting progress
            // is diagnostic; failing to publish does not invalidate the
            // turn — same contract as MessagingToolHandlers' MessageSent
            // emission.
            _logger.LogWarning(ex,
                "sv.progress.report: failed to publish RuntimeProgress for caller {Caller}",
                subject);
        }

        return ParseSchema("""{ "ok": true }""");
    }

    private static JsonElement BuildDetails(string message, double? fraction)
    {
        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("message", message);
            if (fraction is { } f)
            {
                writer.WriteNumber("fraction", f);
            }
            writer.WriteString("source", "mcp:sv.progress.report");
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

    private static double? TryReadDoubleArg(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object ||
            !args.TryGetProperty(name, out var prop))
        {
            return null;
        }
        if (prop.ValueKind != JsonValueKind.Number)
        {
            throw new ArgumentException($"Argument '{name}' must be a number.");
        }
        if (!prop.TryGetDouble(out var value))
        {
            throw new ArgumentException($"Argument '{name}' is not a parseable number.");
        }
        return value;
    }

    private static JsonElement ParseSchema(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
