// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Skills;

using System.Reflection;
using System.Text.Json;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Messaging;

using Microsoft.Extensions.Logging;

/// <summary>
/// Platform <see cref="ISkillRegistry"/> for the message-delivery tools
/// <c>sv.messaging.send</c> and <c>sv.messaging.multicast</c> (ADR-0048 /
/// ADR-0049, reshaped by #2747). Both tools take the same input shape —
/// either an explicit <c>recipients</c> list or a <c>scope</c> — and differ
/// in thread identity: <c>send</c> places every recipient on the single
/// shared thread <c>{caller} ∪ recipients</c>; <c>multicast</c> places each
/// recipient on its own independent 1-1 thread.
/// </summary>
/// <remarks>
/// <para>
/// The agent never names <c>thread_id</c> in either direction: the platform
/// derives it from the participant set per ADR-0030. The caller is
/// auto-included in every thread's participant set — the agent does not
/// list itself in <c>recipients</c>.
/// </para>
/// <para>
/// Recipient kinds are restricted to <c>agent</c> / <c>unit</c> / <c>human</c>
/// at the tool boundary (#2740). The connector scheme (and any other
/// non-routable scheme) is rejected synchronously with a validation-class
/// tool error before tenant / scope resolution happens, so the calling model
/// gets a locally attributable failure instead of a downstream
/// <see cref="MessageDeliveryService.EnsureCanReceive"/> rejection. The
/// downstream guard remains as defence-in-depth on the delivery path.
/// </para>
/// <para>
/// Caller identity is sourced from the MCP session's <see cref="ToolCallContext"/>:
/// <see cref="ToolCallContext.CallerId"/> + <see cref="ToolCallContext.CallerKind"/>
/// build the caller address; the tenant is ambient via <see cref="ITenantContext"/>.
/// The inbound <see cref="ToolCallContext.ThreadId"/> is the caller's upstream
/// thread, used only for hop-budget accounting (#2576) — it never names the
/// delivery thread.
/// </para>
/// </remarks>
public sealed class SvMessagingSkillRegistry : ISkillRegistry
{
    /// <summary>Tool name for <c>sv.messaging.send</c>.</summary>
    public const string SendTool = "sv.messaging.send";

    /// <summary>Tool name for <c>sv.messaging.multicast</c>.</summary>
    public const string MulticastTool = "sv.messaging.multicast";

    /// <summary>Tool name for <c>sv.messaging.respond_to</c>.</summary>
    public const string RespondToTool = "sv.messaging.respond_to";

    private readonly MessagingToolHandlers _handlers;
    private readonly ITenantContext _tenantContext;
    private readonly ILogger _logger;
    private readonly IReadOnlyList<ToolDefinition> _tools;

    public SvMessagingSkillRegistry(
        MessagingToolHandlers handlers,
        ITenantContext tenantContext,
        ILoggerFactory loggerFactory)
    {
        _handlers = handlers;
        _tenantContext = tenantContext;
        _logger = loggerFactory.CreateLogger<SvMessagingSkillRegistry>();

        var assembly = typeof(MessagingToolHandlers).Assembly;
        _tools = new[]
        {
            new ToolDefinition(
                SendTool,
                SchemaDescription(LoadSchema(assembly, "sv.messaging.send.input.schema.json")),
                LoadSchema(assembly, "sv.messaging.send.input.schema.json"),
                ToolCategories.Messaging),
            new ToolDefinition(
                MulticastTool,
                SchemaDescription(LoadSchema(assembly, "sv.messaging.multicast.input.schema.json")),
                LoadSchema(assembly, "sv.messaging.multicast.input.schema.json"),
                ToolCategories.Messaging),
            new ToolDefinition(
                RespondToTool,
                SchemaDescription(LoadSchema(assembly, "sv.messaging.respond_to.input.schema.json")),
                LoadSchema(assembly, "sv.messaging.respond_to.input.schema.json"),
                ToolCategories.Messaging),
        };
    }

    /// <inheritdoc />
    public string Name => "sv";

    /// <inheritdoc />
    public IReadOnlyList<ToolDefinition> GetToolDefinitions() => _tools;

    /// <inheritdoc />
    public Task<JsonElement> InvokeAsync(
        string toolName, JsonElement arguments, CancellationToken cancellationToken = default) =>
        throw new SpringException(
            $"Tool '{toolName}' on the {Name} messaging registry requires caller context. " +
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
            SendTool => InvokeSendAsync(arguments, context, cancellationToken),
            MulticastTool => InvokeMulticastAsync(arguments, context, cancellationToken),
            RespondToTool => InvokeRespondToAsync(arguments, context, cancellationToken),
            _ => throw new SkillNotFoundException(toolName),
        };
    }

    private async Task<JsonElement> InvokeSendAsync(
        JsonElement arguments,
        ToolCallContext context,
        CancellationToken cancellationToken)
    {
        var caller = ResolveCaller(context);
        var upstreamThread = ResolveThreadId(context);
        var (recipients, scope) = ParseRecipientsAndScope(arguments, SendTool);
        var reason = TryGetStringArgument(arguments, "reason", out var reasonValue) ? reasonValue : null;
        var message = BuildMessage(caller, ExtractMessagePayload(arguments, SendTool));

        var result = await _handlers.HandleSendAsync(
            caller,
            _tenantContext.CurrentTenantId,
            recipients,
            scope,
            message,
            reason,
            upstreamThread,
            cancellationToken);

        return SerializeSendResult(result);
    }

    private async Task<JsonElement> InvokeMulticastAsync(
        JsonElement arguments,
        ToolCallContext context,
        CancellationToken cancellationToken)
    {
        var caller = ResolveCaller(context);
        var upstreamThread = ResolveThreadId(context);
        var (recipients, scope) = ParseRecipientsAndScope(arguments, MulticastTool);
        var reason = TryGetStringArgument(arguments, "reason", out var reasonValue) ? reasonValue : null;
        var message = BuildMessage(caller, ExtractMessagePayload(arguments, MulticastTool));

        var result = await _handlers.HandleMulticastAsync(
            caller,
            _tenantContext.CurrentTenantId,
            recipients,
            scope,
            message,
            reason,
            upstreamThread,
            cancellationToken);

        return SerializeMulticastResult(result);
    }

    private async Task<JsonElement> InvokeRespondToAsync(
        JsonElement arguments,
        ToolCallContext context,
        CancellationToken cancellationToken)
    {
        var caller = ResolveCaller(context);
        if (!TryGetStringArgument(arguments, "message_id", out var messageIdValue)
            || !GuidFormatter.TryParse(messageIdValue, out var targetMessageId))
        {
            throw new ArgumentException(
                "sv.messaging.respond_to requires a 'message_id' string naming a message you received " +
                "(the `message_id` from an inbound envelope).");
        }

        var reason = TryGetStringArgument(arguments, "reason", out var reasonValue) ? reasonValue : null;
        var message = BuildMessage(caller, ExtractMessagePayload(arguments, RespondToTool));

        var result = await _handlers.HandleRespondToAsync(
            caller,
            _tenantContext.CurrentTenantId,
            targetMessageId,
            message,
            reason,
            cancellationToken);

        return SerializeSendResult(result);
    }

    private static (IReadOnlyList<Address>? Recipients, MulticastScope? Scope) ParseRecipientsAndScope(
        JsonElement arguments, string toolName)
    {
        // An empty 'recipients' array names no targets, so it counts as
        // "not provided" — otherwise it would slip past the exactly-one gate
        // and reach the delivery path with zero recipients.
        var hasRecipients = arguments.ValueKind == JsonValueKind.Object &&
            arguments.TryGetProperty("recipients", out var recipientsProp) &&
            recipientsProp.ValueKind == JsonValueKind.Array &&
            recipientsProp.GetArrayLength() > 0;
        var hasScope = TryGetStringArgument(arguments, "scope", out var scopeValue);

        if (hasRecipients == hasScope)
        {
            var supplied = hasRecipients ? "both" : "neither";
            throw new ArgumentException(
                $"{toolName} requires EXACTLY ONE of `recipients` or `scope` (you supplied {supplied}). " +
                "Pass `recipients` as a non-empty array of addresses (agent:/unit:/human: — do not list yourself), " +
                "OR `scope` as 'unit-members' or 'siblings'. Use `recipients` to name explicit targets; " +
                "use `scope` to resolve them from your position in the unit graph.");
        }

        if (hasRecipients)
        {
            arguments.TryGetProperty("recipients", out var array);
            var list = new List<Address>(array.GetArrayLength());
            foreach (var element in array.EnumerateArray())
            {
                var raw = element.ValueKind == JsonValueKind.String ? element.GetString() : null;
                if (!Address.TryParse(raw, out var address) || address is null)
                {
                    throw new ArgumentException(
                        $"'{raw}' in `recipients` is not a valid address. Each recipient is a canonical " +
                        "address: agent:<32-hex>, unit:<32-hex>, or human:<32-hex>.");
                }
                EnsureRoutableRecipientScheme(address, toolName);
                list.Add(address);
            }
            return (list, null);
        }

        if (!TryParseMulticastScope(scopeValue, out var scope))
        {
            throw new ArgumentException(
                $"'{scopeValue}' is not a valid scope. Use 'unit-members' or 'siblings'.");
        }
        return (null, scope);
    }

    /// <summary>
    /// Rejects recipients whose scheme is not one of the routable kinds
    /// (<c>agent</c> / <c>unit</c> / <c>human</c>) at the tool boundary
    /// (#2740). The downstream <see cref="MessageDeliveryService.EnsureCanReceive"/>
    /// catches the same condition with the <c>UnroutableTarget</c> reject
    /// code, but raising synchronously here means the calling model sees a
    /// validation-class tool error before any tenant / scope resolution
    /// happens — the failure is locally attributable to the argument the
    /// model produced. Connectors are the canonical case: they translate
    /// external events into inbound messages but do not host mailboxes;
    /// the same guard rejects other non-routable schemes (for example the
    /// auth <c>tenant-user</c> scheme) up front.
    /// </summary>
    private static void EnsureRoutableRecipientScheme(Address recipient, string toolName)
    {
        if (string.Equals(recipient.Scheme, Address.AgentScheme, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(recipient.Scheme, Address.UnitScheme, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(recipient.Scheme, Address.HumanScheme, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new ArgumentException(
            $"{toolName} cannot deliver to '{recipient}': the '{recipient.Scheme}' scheme is non-routable " +
            $"(UnroutableTarget). Valid recipient kinds are '{Address.AgentScheme}', '{Address.UnitScheme}', " +
            $"and '{Address.HumanScheme}'. Connectors ('{Address.ConnectorScheme}:<uuid>') appear as the " +
            "sender of inbound messages but cannot receive — pick a human, agent, or unit recipient instead.");
    }

    // internal (not private) so a unit test can pin the canonical no-dash
    // messageId wire form the orchestrator correlates against (#3088).
    internal static JsonElement SerializeSendResult(SendResult result)
    {
        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            // Canonical no-dash wire form (GuidFormatter), matching the inbound
            // envelope's `message_id` / `in_reply_to`. A consumer correlates the
            // id this ack returns against a later reply's `in_reply_to`; if the
            // two surfaces format the same Guid differently (dashed here vs
            // no-dash there) the match silently fails. (#3088)
            writer.WriteString("messageId", GuidFormatter.Format(result.MessageId));
            writer.WritePropertyName("deliveries");
            writer.WriteStartArray();
            foreach (var delivery in result.Deliveries)
            {
                WriteDelivery(writer, delivery);
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    internal static JsonElement SerializeMulticastResult(MulticastResult result)
    {
        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            // Canonical no-dash wire form — see SerializeSendResult (#3088).
            writer.WriteString("messageId", GuidFormatter.Format(result.MessageId));
            writer.WritePropertyName("deliveries");
            writer.WriteStartArray();
            foreach (var delivery in result.Deliveries)
            {
                WriteDelivery(writer, delivery);
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    private static void WriteDelivery(Utf8JsonWriter writer, RecipientDeliveryAck delivery)
    {
        writer.WriteStartObject();
        writer.WriteString("target", delivery.Target.ToString());
        writer.WriteBoolean("delivered", delivery.Delivered);
        if (delivery.Error is { } err)
        {
            writer.WriteString("error", err);
        }
        else
        {
            writer.WriteNull("error");
        }
        writer.WriteEndObject();
    }

    /// <summary>
    /// Resolves the caller address from the active MCP session's
    /// <see cref="ToolCallContext"/>. The session always materialises a
    /// Guid-shaped caller id and a subject scheme, so a parse failure here
    /// is a wiring bug, not a routine caller error.
    /// </summary>
    private static Address ResolveCaller(ToolCallContext context)
    {
        if (string.IsNullOrWhiteSpace(context.CallerId)
            || !GuidFormatter.TryParse(context.CallerId, out var callerGuid))
        {
            throw new SpringException(
                "sv.messaging.* requires a caller id; the active MCP session did not supply one.");
        }

        var callerKind = string.IsNullOrWhiteSpace(context.CallerKind)
            ? Address.AgentScheme
            : context.CallerKind;
        return new Address(callerKind, callerGuid);
    }

    private static Guid ResolveThreadId(ToolCallContext context) =>
        GuidFormatter.TryParse(context.ThreadId, out var parsed) ? parsed : Guid.Empty;

    /// <summary>
    /// Builds the <see cref="Message"/> envelope for a delivery. <c>ThreadId</c>
    /// is intentionally left <c>null</c> (#2596 / ADR-0030): the delivery hop
    /// owns thread identity, so <see cref="MessageDeliveryService"/> resolves
    /// the thread from the participant set per hop. The id is freshly minted
    /// per outbound message — the inbound-turn id (carried on
    /// <see cref="ToolCallContext.MessageId"/>) is the *cause* of the turn,
    /// not the *identity* of the reply. Stamping the inbound id onto the
    /// outbound message would dedupe-skip in <c>EfMessageWriter</c> and
    /// collide on activity-event correlation (#2765). <c>To</c> is set to
    /// the caller as a placeholder; the delivery loop overrides it per
    /// recipient.
    /// </summary>
    private static Message BuildMessage(Address caller, JsonElement payload) =>
        new(
            Guid.NewGuid(),
            caller,
            caller,
            MessageType.Domain,
            ThreadId: null,
            payload,
            DateTimeOffset.UtcNow);

    /// <summary>
    /// Builds the delivery payload from the <c>message</c> argument and
    /// rejects content-less messages (#3035). A string becomes
    /// <c>{ content }</c>; an object is taken verbatim; anything else
    /// (absent, JSON <c>null</c>, number, boolean, array) yields no
    /// deliverable body. <see cref="EnsureDeliverableContent"/> then fails
    /// the call synchronously with a retry-guiding tool error rather than
    /// delivering an empty envelope a recipient reads as "nothing sent" —
    /// the root cause of the #3034 false-alarm cascade.
    /// </summary>
    private static JsonElement ExtractMessagePayload(JsonElement arguments, string toolName)
    {
        var payload = ResolveMessagePayload(arguments);
        EnsureDeliverableContent(payload, toolName);
        return payload;
    }

    private static JsonElement ResolveMessagePayload(JsonElement arguments)
    {
        if (arguments.ValueKind == JsonValueKind.Object &&
            arguments.TryGetProperty("message", out var message))
        {
            return message.ValueKind switch
            {
                JsonValueKind.String =>
                    JsonSerializer.SerializeToElement(new { content = message.GetString() }),
                JsonValueKind.Object => message,
                _ => JsonSerializer.SerializeToElement(new { }),
            };
        }

        return JsonSerializer.SerializeToElement(new { });
    }

    /// <summary>
    /// Well-known top-level string properties that carry a human-readable
    /// body, mirroring the payload renderers in
    /// <c>Cvoya.Spring.Core.Messaging.Rendering</c> (<c>content</c> for the
    /// <c>sv.messaging.*</c> string wrap, plus the <c>text</c> / <c>body</c>
    /// / <c>Output</c> shapes other producers use). A payload whose only
    /// properties are these — and all empty/whitespace — renders to an empty
    /// bubble, so it is treated as content-less.
    /// </summary>
    private static readonly HashSet<string> BodyProperties =
        new(StringComparer.Ordinal) { "content", "text", "body", "Output" };

    /// <summary>
    /// Rejects a delivery payload that carries no readable content (#3035).
    /// The #3034 cascade started with <c>sv.messaging.respond_to</c> fired as
    /// a content-less acknowledgement: the tool accepted it and delivered an
    /// empty <c>{}</c> recipients read as "didn't send / missing". A payload
    /// is content-less when it has no properties, or when its only properties
    /// are well-known body fields that are all empty/whitespace. Any other
    /// property is opaque structured content the recipient sees as JSON in
    /// the inbound envelope, so well-formed structured sends are unaffected.
    /// </summary>
    private static void EnsureDeliverableContent(JsonElement payload, string toolName)
    {
        if (payload.ValueKind == JsonValueKind.Object && PayloadHasContent(payload))
        {
            return;
        }

        throw new ArgumentException(
            $"{toolName} requires a non-empty message. Put your text in the `message` argument — " +
            "a non-empty string, or an object with a non-empty `content` (or `text` / `body`) field. " +
            "An empty or whitespace-only message delivers an empty envelope the recipient reads as " +
            "\"nothing was sent\". If you have nothing to add, do not call a messaging tool — there is no empty acknowledgement.");
    }

    private static bool PayloadHasContent(JsonElement payload)
    {
        foreach (var property in payload.EnumerateObject())
        {
            if (BodyProperties.Contains(property.Name))
            {
                if (property.Value.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(property.Value.GetString()))
                {
                    return true;
                }
            }
            else
            {
                // A non-body property is opaque structured content the
                // recipient sees as JSON in the inbound envelope — meaningful.
                return true;
            }
        }

        return false;
    }

    private static bool TryGetStringArgument(JsonElement arguments, string name, out string? value)
    {
        value = null;
        if (arguments.ValueKind == JsonValueKind.Object &&
            arguments.TryGetProperty(name, out var prop) &&
            prop.ValueKind == JsonValueKind.String)
        {
            value = prop.GetString();
            return value is not null;
        }

        return false;
    }

    private static bool TryParseMulticastScope(string? value, out MulticastScope? scope)
    {
        scope = value switch
        {
            "unit-members" => MulticastScope.UnitMembers,
            "siblings" => MulticastScope.Siblings,
            _ => null,
        };

        return scope is not null;
    }

    private static string SchemaDescription(JsonElement inputSchema)
    {
        if (inputSchema.ValueKind == JsonValueKind.Object &&
            inputSchema.TryGetProperty("description", out var description) &&
            description.ValueKind == JsonValueKind.String)
        {
            return description.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    private static JsonElement LoadSchema(Assembly assembly, string fileName)
    {
        var resourceName = $"Cvoya.Spring.Dapr.Messaging.Resources.{fileName}";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded messaging-tool schema '{resourceName}' was not found. " +
                "Verify the EmbeddedResource glob in Cvoya.Spring.Dapr.csproj.");

        using var document = JsonDocument.Parse(stream);
        return document.RootElement.Clone();
    }
}
