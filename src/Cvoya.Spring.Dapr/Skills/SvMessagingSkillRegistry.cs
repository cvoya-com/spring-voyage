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
/// ADR-0049). It re-fronts the existing <see cref="MessagingToolHandlers"/>
/// delivery seam as a registry so messaging joins the other
/// <c>Sv*SkillRegistry</c> types on the single platform MCP server (ADR-0051):
/// one auth model (the MCP session bearer token), one JSON-RPC handler, and —
/// critically — the same effective-grant gate (#2379) and unit-policy
/// enforcement (#162) every other <c>sv.*</c> tool already passes through.
/// </summary>
/// <remarks>
/// <para>
/// The retired callback JWT carried per-turn delivery authority as four
/// claims (<c>tenantId</c> / <c>agentAddress</c> / <c>threadId</c> /
/// <c>messageId</c>). ADR-0051 folds that authority into the MCP session,
/// which is minted per turn and revoked on turn-end. This registry reads it
/// from <see cref="ToolCallContext"/>: <see cref="ToolCallContext.CallerId"/>
/// + <see cref="ToolCallContext.CallerKind"/> give the caller address,
/// <see cref="ToolCallContext.ThreadId"/> gives the thread,
/// <see cref="ToolCallContext.MessageId"/> gives the inbound message, and the
/// tenant is ambient via <see cref="ITenantContext"/>.
/// </para>
/// <para>
/// Because <c>sv.messaging.*</c> tools live in the <c>sv</c> namespace, the
/// effective-grant resolver's platform tier (<c>EnumeratePlatformTools</c>)
/// surfaces them implicitly for every agent and unit subject — no grant row
/// is required. That is the "default grant seeding" ADR-0051 §4 calls for:
/// existing agents keep their messaging tools by construction, while a unit
/// policy can now <i>deny</i> messaging, closing the gap the two-server split
/// left open.
/// </para>
/// <para>
/// The ADR-0049 delivery-acknowledgement contract is unchanged — each tool is
/// an RPC returning a delivery ack, never the recipient's reply; delivery is
/// synchronous with bounded retry; failure surfaces as a synchronous tool
/// error. Only the transport and the credential moved.
/// </para>
/// </remarks>
public sealed class SvMessagingSkillRegistry : ISkillRegistry
{
    /// <summary>Tool name for <c>sv.messaging.send</c>.</summary>
    public const string SendTool = "sv.messaging.send";

    /// <summary>Tool name for <c>sv.messaging.multicast</c>.</summary>
    public const string MulticastTool = "sv.messaging.multicast";

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
            _ => throw new SkillNotFoundException(toolName),
        };
    }

    private async Task<JsonElement> InvokeSendAsync(
        JsonElement arguments,
        ToolCallContext context,
        CancellationToken cancellationToken)
    {
        var caller = ResolveCaller(context);
        var threadId = ResolveThreadId(context);

        if (!TryGetStringArgument(arguments, "address", out var addressValue))
        {
            throw new ArgumentException("sv.messaging.send requires an 'address' string argument.");
        }

        if (!Address.TryParse(addressValue, out var target) || target is null)
        {
            throw new ArgumentException($"'{addressValue}' is not a valid Spring Voyage address.");
        }

        var reason = TryGetStringArgument(arguments, "reason", out var reasonValue) ? reasonValue : null;
        var message = BuildMessage(context, caller, target, ExtractMessagePayload(arguments));

        var ack = await _handlers.HandleSendAsync(
            caller,
            _tenantContext.CurrentTenantId,
            target,
            message,
            reason,
            threadId,
            cancellationToken);

        return JsonSerializer.SerializeToElement(new
        {
            delivered = ack.Delivered,
            messageId = ack.MessageId.ToString("D"),
            target = ack.Target.ToString(),
            threadId = ack.ThreadId.ToString("D"),
        });
    }

    private async Task<JsonElement> InvokeMulticastAsync(
        JsonElement arguments,
        ToolCallContext context,
        CancellationToken cancellationToken)
    {
        var caller = ResolveCaller(context);
        var threadId = ResolveThreadId(context);

        var hasAddresses = arguments.ValueKind == JsonValueKind.Object &&
            arguments.TryGetProperty("addresses", out var addressesProp) &&
            addressesProp.ValueKind == JsonValueKind.Array;
        var hasScope = TryGetStringArgument(arguments, "scope", out var scopeValue);

        if (hasAddresses == hasScope)
        {
            throw new ArgumentException(
                "sv.messaging.multicast requires exactly one of an 'addresses' array or a 'scope' string.");
        }

        List<Address>? targets = null;
        MulticastScope? scope = null;

        if (hasAddresses)
        {
            targets = new List<Address>();
            arguments.TryGetProperty("addresses", out var addressesArray);
            foreach (var element in addressesArray.EnumerateArray())
            {
                var addressValue = element.ValueKind == JsonValueKind.String ? element.GetString() : null;
                if (!Address.TryParse(addressValue, out var target) || target is null)
                {
                    throw new ArgumentException($"'{addressValue}' is not a valid Spring Voyage address.");
                }

                targets.Add(target);
            }
        }
        else if (!TryParseMulticastScope(scopeValue, out scope))
        {
            throw new ArgumentException(
                $"'{scopeValue}' is not a valid multicast scope. Use 'unit-members' or 'siblings'.");
        }

        var reason = TryGetStringArgument(arguments, "reason", out var reasonValue) ? reasonValue : null;
        var message = BuildMessage(
            context,
            caller,
            targets is { Count: > 0 } ? targets[0] : caller,
            ExtractMessagePayload(arguments));

        var result = await _handlers.HandleMulticastAsync(
            caller,
            _tenantContext.CurrentTenantId,
            targets,
            scope,
            message,
            reason,
            threadId,
            cancellationToken);

        return JsonSerializer.SerializeToElement(new
        {
            messageId = result.MessageId.ToString("D"),
            threadId = result.ThreadId.ToString("D"),
            deliveries = result.Deliveries
                .Select(outcome => new
                {
                    target = outcome.Target.ToString(),
                    delivered = outcome.Delivered,
                    error = outcome.Error,
                })
                .ToArray(),
        });
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
    /// the thread from the <c>(caller, target)</c> participant set per hop. The
    /// message id is the inbound-turn message from the session — the per-turn
    /// delivery authority the callback JWT used to carry as its <c>messageId</c>
    /// claim (ADR-0051).
    /// </summary>
    private static Message BuildMessage(
        ToolCallContext context, Address caller, Address target, JsonElement payload) =>
        new(
            context.MessageId,
            caller,
            target,
            MessageType.Domain,
            ThreadId: null,
            payload,
            DateTimeOffset.UtcNow);

    private static JsonElement ExtractMessagePayload(JsonElement arguments)
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
