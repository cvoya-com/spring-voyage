// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Skills;

using System.Text.Json;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Orchestration;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Core.Tenancy;

using Microsoft.Extensions.Logging;

/// <summary>
/// Platform-level <see cref="ISkillRegistry"/> for the SV runtime
/// reflection tools (issue #2493). It exposes:
/// <list type="bullet">
///   <item>
///     <c>sv.report_progress</c> — publishes a narrative progress event
///     onto the platform's activity bus from inside MCP rather than via
///     the SDK's OTLP emitter. Both paths produce a
///     <see cref="ActivityEventType.RuntimeProgress"/> event with the
///     same shape; the MCP tool is parity for runtimes that don't use
///     the SDK's helper.
///   </item>
///   <item>
///     <c>sv.report_decision</c> — records a structured routing /
///     delegation decision as a <see cref="ActivityEventType.DecisionMade"/>
///     activity, <i>independent of whether the decision executed</i>
///     (issue #2581). The orchestration tools (<c>delegate_to</c> /
///     <c>fanout_to</c>) only emit a decision when they actually run;
///     when a runtime decides to route but cannot execute — the tool is
///     unavailable, the model fails to call it, or the call is rejected
///     before delivery — this tool is the always-available channel that
///     keeps the decision from being silently lost. It lives on the
///     <c>sv.*</c> surface, which uses the long-lived MCP token, so it
///     stays reachable even when the short-lived orchestration callback
///     token has expired.
///   </item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// <b>Reconciliation with the orchestration tools.</b> A successfully
/// executed <c>delegate_to</c> / <c>fanout_to</c> already emits exactly
/// one <see cref="ActivityEventType.DecisionMade"/> activity from the
/// dispatcher's <c>OrchestrationToolHandlers</c>. The runtime prompt
/// directs <c>sv.report_decision</c> to be called <i>only</i> for the
/// not-executed case, so the happy path still produces one decision
/// record, not two.
/// </para>
/// </remarks>
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

    /// <summary>Tool name for <c>sv.report_decision</c> (issue #2581).</summary>
    public const string ReportDecisionTool = "sv.report_decision";

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

    private static readonly JsonElement ReportDecisionArgSchema = ParseSchema("""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["targets", "outcome"],
          "properties": {
            "kind": {
              "type": "string",
              "enum": ["delegate", "fanout"],
              "description": "The kind of routing decision: 'delegate' for a single target, 'fanout' for several. Defaults to 'delegate'."
            },
            "targets": {
              "type": "array",
              "minItems": 1,
              "items": { "type": "string" },
              "description": "The intended target(s) — a canonical Spring Voyage address or, if that is all you have, the target's name. One entry for a delegate, several for a fanout."
            },
            "rationale": {
              "type": "string",
              "description": "Why this routing was chosen — the same rationale you would pass as the 'reason' argument to delegate_to / fanout_to."
            },
            "outcome": {
              "type": "string",
              "enum": ["tool_unavailable", "validation_rejected", "delivery_failed", "not_attempted"],
              "description": "Why the decision did NOT execute. Call this tool ONLY when the decision could not be carried out: 'tool_unavailable' (delegate_to / fanout_to is not in your tool surface), 'validation_rejected' (the platform rejected the call), 'delivery_failed' (the message could not be delivered), or 'not_attempted'. A decision that DID execute is already recorded by delegate_to / fanout_to — do not also report it here."
            },
            "detail": {
              "type": "string",
              "description": "Optional free-text detail about the not-executed reason (e.g. the validation error text)."
            }
          }
        }
        """);

    private readonly IOtlpIngestService _ingestService;
    private readonly ITenantContext _tenantContext;
    private readonly IActivityEventBus _activityEventBus;
    private readonly ILogger _logger;

    private readonly IReadOnlyList<ToolDefinition> _tools;

    public SvRuntimeSkillRegistry(
        IOtlpIngestService ingestService,
        ITenantContext tenantContext,
        IActivityEventBus activityEventBus,
        ILoggerFactory loggerFactory)
    {
        _ingestService = ingestService;
        _tenantContext = tenantContext;
        _activityEventBus = activityEventBus;
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
            new ToolDefinition(
                ReportDecisionTool,
                "Record a structured routing/delegation decision that you could NOT " +
                "execute, so it is not silently lost. Call this when you decided to " +
                "route work to a target but delegate_to / fanout_to was unavailable, " +
                "rejected, or otherwise did not run. Surfaces as a DecisionMade " +
                "activity naming the intended target(s) and the reason it did not " +
                "execute. Do NOT call this for a delegation that succeeded — a " +
                "successful delegate_to / fanout_to already records its own decision.",
                ReportDecisionArgSchema),
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
    public Task<JsonElement> InvokeAsync(
        string toolName,
        JsonElement arguments,
        ToolCallContext context,
        CancellationToken cancellationToken = default)
    {
        return toolName switch
        {
            ReportProgressTool => InvokeReportProgressAsync(arguments, context, cancellationToken),
            ReportDecisionTool => InvokeReportDecisionAsync(arguments, context, cancellationToken),
            _ => throw new SkillNotFoundException(toolName),
        };
    }

    private async Task<JsonElement> InvokeReportProgressAsync(
        JsonElement arguments,
        ToolCallContext context,
        CancellationToken cancellationToken)
    {
        var text = RequireStringArg(arguments, "text");
        var kind = TryReadStringArg(arguments, "kind");

        if (string.IsNullOrWhiteSpace(context.CallerId)
            || !GuidFormatter.TryParse(context.CallerId, out var callerGuid))
        {
            throw new SpringException(
                $"Tool '{ReportProgressTool}' requires a caller id; the active MCP session did not supply one.");
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

    /// <summary>
    /// #2581: records a routing/delegation decision the runtime made but
    /// could not execute as a structured <see cref="ActivityEventType.DecisionMade"/>
    /// activity, so it is visible on the activity stream even though
    /// <c>delegate_to</c> / <c>fanout_to</c> never ran.
    /// </summary>
    private async Task<JsonElement> InvokeReportDecisionAsync(
        JsonElement arguments,
        ToolCallContext context,
        CancellationToken cancellationToken)
    {
        var targetStrings = ReadTargetStrings(arguments);
        var outcome = RequireEnumArg(
            arguments,
            "outcome",
            "tool_unavailable",
            "validation_rejected",
            "delivery_failed",
            "not_attempted");
        var kind = string.Equals(TryReadStringArg(arguments, "kind"), "fanout", StringComparison.Ordinal)
            ? OrchestrationDecisionKind.Fanout
            : OrchestrationDecisionKind.Delegate;
        var rationale = TryReadStringArg(arguments, "rationale");
        var detail = TryReadStringArg(arguments, "detail");

        if (string.IsNullOrWhiteSpace(context.CallerId)
            || !GuidFormatter.TryParse(context.CallerId, out var callerGuid))
        {
            throw new SpringException(
                $"Tool '{ReportDecisionTool}' requires a caller id; the active MCP session did not supply one.");
        }
        var callerKind = string.IsNullOrWhiteSpace(context.CallerKind)
            ? Address.AgentScheme
            : context.CallerKind;
        var subject = new Address(callerKind, callerGuid);

        var threadId = GuidFormatter.TryParse(context.ThreadId, out var parsedThreadId)
            ? parsedThreadId
            : Guid.Empty;

        // A decision the runtime could not execute may name its target by
        // canonical address or by a human-facing name (the runtime knows
        // members by name via sv.get_member). Parse what is a canonical
        // address into OrchestrationDecision.Targets; the verbatim
        // strings always go onto the metadata so the intended target is
        // never lost even when it was a name.
        var parsedTargets = targetStrings
            .Select(raw => Address.TryParse(raw, out var a) && a is not null ? a : null)
            .Where(a => a is not null)
            .Select(a => a!)
            .ToArray();

        var decision = new OrchestrationDecision(
            DecisionId: Guid.NewGuid(),
            TenantId: _tenantContext.CurrentTenantId,
            UnitAddress: subject,
            ThreadId: threadId,
            InputMessageId: Guid.Empty,
            Kind: kind,
            Targets: parsedTargets,
            // The runtime decided but never executed — distinct from a
            // delivery that was attempted and Failed.
            Status: OrchestrationDecisionStatus.NotExecuted,
            ResultMessageIds: [],
            Reason: rationale,
            Metadata: BuildDecisionMetadata(outcome, detail, targetStrings),
            CreatedAt: DateTimeOffset.UtcNow);

        var summary = kind == OrchestrationDecisionKind.Fanout
            ? $"Routing decision to {targetStrings.Count} target(s) not executed ({outcome})."
            : $"Routing decision to '{targetStrings[0]}' not executed ({outcome}).";

        var activityEvent = new ActivityEvent(
            Guid.NewGuid(),
            decision.CreatedAt,
            subject,
            ActivityEventType.DecisionMade,
            // A decision that could not execute is an operator-actionable
            // condition — surface it at Warning, matching the dispatcher's
            // failed-decision severity.
            ActivitySeverity.Warning,
            summary,
            JsonSerializer.SerializeToElement(decision),
            threadId == Guid.Empty ? null : threadId.ToString("D"));

        try
        {
            await _activityEventBus.PublishAsync(activityEvent, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort: never raise back to the model — recording the
            // decision is diagnostic, not load-bearing for the turn.
            _logger.LogWarning(ex,
                "sv.report_decision: failed to publish DecisionMade for caller {Caller}", subject);
        }

        return ParseSchema("""{ "ok": true }""");
    }

    private static IReadOnlyList<string> ReadTargetStrings(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty("targets", out var targetsProp)
            || targetsProp.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("Missing required argument 'targets' (array of target strings).");
        }

        var targets = new List<string>();
        foreach (var element in targetsProp.EnumerateArray())
        {
            var raw = element.ValueKind == JsonValueKind.String ? element.GetString() : null;
            if (string.IsNullOrWhiteSpace(raw))
            {
                throw new ArgumentException("Argument 'targets' entries must be non-empty strings.");
            }
            targets.Add(raw);
        }

        if (targets.Count == 0)
        {
            throw new ArgumentException("Argument 'targets' must contain at least one target.");
        }

        return targets;
    }

    private static string RequireEnumArg(JsonElement args, string name, params string[] allowed)
    {
        var raw = RequireStringArg(args, name);
        if (Array.IndexOf(allowed, raw) < 0)
        {
            throw new ArgumentException(
                $"Argument '{name}' must be one of: {string.Join(", ", allowed)}. Got '{raw}'.");
        }
        return raw;
    }

    private static JsonElement BuildDecisionMetadata(
        string outcome,
        string? detail,
        IReadOnlyList<string> intendedTargets)
    {
        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            // Machine-readable not-executed reason — the platform cannot
            // parse free prose, so the runtime emits it through this
            // defined field.
            writer.WriteString("executionOutcome", outcome);
            // Verbatim intended-target strings, even when a target was a
            // human name rather than a canonical address — so the intended
            // target is never lost.
            writer.WriteStartArray("intendedTargets");
            foreach (var target in intendedTargets)
            {
                writer.WriteStringValue(target);
            }
            writer.WriteEndArray();
            if (!string.IsNullOrWhiteSpace(detail))
            {
                writer.WriteString("detail", detail);
            }
            writer.WriteString("source", "mcp:sv.report_decision");
            writer.WriteEndObject();
        }
        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
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
