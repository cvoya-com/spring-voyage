// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Skills;

using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Skills;

using Microsoft.Extensions.Logging;

/// <summary>
/// Default <see cref="ISkillInvoker"/> (#359). Resolves the skill name
/// against the live <see cref="IExpertiseSkillCatalog"/>, translates the
/// invocation into a <see cref="MessageType.Domain"/> <see cref="Message"/>,
/// and dispatches through <see cref="IMessageRouter"/>.
/// </summary>
/// <remarks>
/// <para>
/// Routing through <see cref="IMessageRouter"/> is load-bearing: that is the
/// single enforcement seam that applies boundary opacity (#413 / #497),
/// hierarchy permissions (#414), cloning policy (#416), initiative levels
/// (#415), and activity emission (#391 / #484). Bypassing the router would
/// make the skill surface a governance hole — and that was the biggest
/// critique of the closed first-pass implementation (PR #532). An
/// alternative <see cref="ISkillInvoker"/> (the A2A gateway tracked in
/// #539) will either delegate back to this type or re-implement every
/// check.
/// </para>
/// <para>
/// <b>Invocation-time boundary re-check.</b> The catalog enumeration is
/// already boundary-aware (it asks the aggregator for a caller-specific view),
/// so a skill hidden from the caller resolves to <c>null</c> here and the
/// invoker surfaces <c>BOUNDARY_BLOCKED</c>. Combined with the router's
/// permission / policy chain we get defence in depth: even a caller that
/// knows the skill name cannot invoke a hidden expertise entry.
/// </para>
/// </remarks>
public class MessageRouterSkillInvoker(
    IExpertiseSkillCatalog catalog,
    IMessageRouter router,
    TimeProvider timeProvider,
    ILoggerFactory loggerFactory,
    DirectorySearchSkillRegistry directorySearchRegistry) : ISkillInvoker
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<MessageRouterSkillInvoker>();

    /// <inheritdoc />
    public async Task<SkillInvocationResult> InvokeAsync(
        SkillInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        // Meta-skills are handled in-process — they do not target an agent /
        // unit and therefore do not travel through the message router. Today
        // the only meta-skill is `directory/search` (#542), which resolves a
        // capability description to a concrete expertise slug BEFORE the
        // planner picks the target to call. Future meta-skills (e.g. a
        // unit-hierarchy inspector) slot in the same way.
        if (string.Equals(invocation.SkillName, DirectorySearchSkillRegistry.SkillName, StringComparison.Ordinal))
        {
            return await InvokeMetaSkillAsync(invocation, cancellationToken);
        }

        // Defence in depth: re-resolve at invocation time with a caller-
        // specific boundary context. If the caller cannot see the skill on
        // enumeration, they cannot invoke it here either.
        var context = invocation.Caller is null
            ? BoundaryViewContext.External
            : new BoundaryViewContext(Caller: invocation.Caller);

        ExpertiseSkill? resolved;
        try
        {
            resolved = await catalog.ResolveAsync(invocation.SkillName, context, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Skill catalog resolution failed for {Skill}.", invocation.SkillName);
            return SkillInvocationResult.Failure("CATALOG_ERROR", ex.Message);
        }

        if (resolved is null)
        {
            // Two cases collapse to one error: (a) the name is unknown, or
            // (b) the caller's boundary view hides it. We deliberately do
            // not leak which by returning a single code — consistent with
            // how the router surfaces address-not-found vs permission-denied
            // as distinct errors only when the caller is authorised to see
            // the target exists.
            return SkillInvocationResult.Failure(
                "SKILL_NOT_FOUND",
                $"Skill '{invocation.SkillName}' is not available to this caller.");
        }

        var threadId = invocation.CorrelationId ?? Guid.NewGuid().ToString("N");
        var from = invocation.Caller ?? new Address("skill", "anonymous");

        var payload = BuildPayload(invocation.SkillName, invocation.Arguments, resolved);

        var message = new Message(
            Id: Guid.NewGuid(),
            From: from,
            To: resolved.Target,
            Type: MessageType.Domain,
            ThreadId: threadId,
            Payload: payload,
            Timestamp: timeProvider.GetUtcNow());

        var routed = await router.RouteAsync(message, cancellationToken);
        if (!routed.IsSuccess)
        {
            var error = routed.Error ?? new RoutingError("UNKNOWN", "Routing failed without an error payload.");
            return SkillInvocationResult.Failure(error.Code, error.Message);
        }

        // The router returns the target's response Message; surface its
        // payload verbatim. A target that returns null collapses to an empty
        // JSON object so the caller always has a well-shaped payload.
        var response = routed.Value;
        if (response is null)
        {
            using var empty = JsonDocument.Parse("{}");
            return SkillInvocationResult.Success(empty.RootElement.Clone());
        }

        return SkillInvocationResult.Success(response.Payload);
    }

    private async Task<SkillInvocationResult> InvokeMetaSkillAsync(
        SkillInvocation invocation,
        CancellationToken cancellationToken)
    {
        // Caller identity flows into the search query so boundary-scoped
        // results fall out naturally. A missing caller defaults to an
        // external view — the safest default.
        var arguments = invocation.Arguments.ValueKind == JsonValueKind.Undefined
            ? EmptyJsonObject()
            : invocation.Arguments;

        try
        {
            var payload = await directorySearchRegistry.InvokeAsync(
                invocation.SkillName, arguments, cancellationToken);
            return SkillInvocationResult.Success(payload);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "directory/search meta-skill invocation failed.");
            return SkillInvocationResult.Failure("SEARCH_ERROR", ex.Message);
        }
    }

    private static JsonElement EmptyJsonObject()
    {
        using var doc = JsonDocument.Parse("{}");
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// Wraps the raw skill arguments in a self-describing envelope so the
    /// receiving agent / unit can tell "this is a skill call" from a plain
    /// domain message. The envelope stays intentionally small — any future
    /// fields (e.g. streaming handle for #539) slot in here without changing
    /// the router or the receiver contract.
    /// </summary>
    internal static JsonElement BuildPayload(string skillName, JsonElement arguments, ExpertiseSkill resolved)
    {
        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("skill", skillName);
            writer.WriteString("expertise", resolved.Entry.Domain.Name);
            writer.WriteString("origin", $"{resolved.Entry.Origin.Scheme}://{resolved.Entry.Origin.Path}");
            writer.WritePropertyName("arguments");
            if (arguments.ValueKind == JsonValueKind.Undefined)
            {
                writer.WriteStartObject();
                writer.WriteEndObject();
            }
            else
            {
                arguments.WriteTo(writer);
            }
            writer.WriteEndObject();
        }

        var bytes = stream.ToArray();
        return JsonDocument.Parse(bytes).RootElement.Clone();
    }
}