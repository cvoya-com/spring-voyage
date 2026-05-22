// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Mcp;

using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Policies;
using Cvoya.Spring.Core.Skills;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// In-process MCP server that exposes <see cref="ISkillRegistry"/> tools to
/// containerised agents over HTTP + JSON-RPC 2.0. Implements the minimum
/// subset of MCP needed to exchange tool calls: <c>initialize</c>,
/// <c>tools/list</c>, and <c>tools/call</c>. Streaming and notifications are
/// intentionally out of scope — GitHub connector calls are short RPCs.
/// </summary>
/// <remarks>
/// <para>
/// Auth model: the dispatcher calls <see cref="IssueSession"/> before launching
/// a container and hands the resulting bearer token to the container via the
/// per-turn A2A <c>message/send</c> metadata. The server validates the
/// <c>Authorization: Bearer &lt;token&gt;</c> header on every request and binds
/// the call to the issued <see cref="McpSession"/>. Tokens are
/// single-agent/single-thread and revoked when the invocation completes.
/// </para>
/// <para>
/// ADR-0052 / Wave 3 (#2625): the MCP surface is served as a minimal-API
/// route on the worker's existing Kestrel host — see
/// <c>WorkerComposition</c> for the Kestrel endpoint + route wiring. The route
/// delegates to <see cref="HandleRequestAsync"/>. <see cref="McpServer"/> is no
/// longer an <c>IHostedService</c> and no longer owns a port listener; it owns
/// the session store and the JSON-RPC dispatch only.
/// </para>
/// </remarks>
public class McpServer : IMcpServer
{
    private readonly IReadOnlyList<ISkillRegistry> _registries;
    private readonly Dictionary<string, ISkillRegistry> _toolToRegistry;
    private readonly McpServerOptions _options;
    private readonly ILogger _logger;
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly IActivityEventBus? _activityEventBus;
    private readonly ConcurrentDictionary<string, McpSession> _sessions = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes the server with the set of registries to expose.
    /// </summary>
    /// <remarks>
    /// <paramref name="scopeFactory"/> is optional so standalone / test
    /// constructions can continue to instantiate the server directly. When
    /// it is supplied, every <c>tools/call</c> resolves an
    /// <see cref="IUnitPolicyEnforcer"/> from a fresh scope and consults the
    /// unit-policy framework (#162) before dispatching to the underlying
    /// <see cref="ISkillRegistry"/>. Denials are surfaced to the model as a
    /// tool error (isError=true) so the agent's thread can see the
    /// block and adapt.
    /// </remarks>
    public McpServer(
        IEnumerable<ISkillRegistry> registries,
        IOptions<McpServerOptions> options,
        ILoggerFactory loggerFactory,
        IServiceScopeFactory? scopeFactory = null,
        IActivityEventBus? activityEventBus = null)
    {
        _registries = registries.ToList();
        _options = options.Value;
        _logger = loggerFactory.CreateLogger<McpServer>();
        _scopeFactory = scopeFactory;
        _activityEventBus = activityEventBus;

        // ADR-0052 §3: the container-facing endpoint is derived from
        // configuration, not from a started listener. The MCP surface is now
        // a Kestrel route on the worker (#2625); there is no listener whose
        // bound port has to be observed. Endpoint-only consumers
        // (PersistentAgentLifecycle, A2AExecutionDispatcher) read it the same
        // way they always have, but it is now non-null from construction.
        Endpoint = _options.ContainerEndpoint;

        _toolToRegistry = new Dictionary<string, ISkillRegistry>(StringComparer.Ordinal);
        foreach (var registry in _registries)
        {
            foreach (var tool in registry.GetToolDefinitions())
            {
                if (_toolToRegistry.ContainsKey(tool.Name))
                {
                    throw new SpringException(
                        $"Tool '{tool.Name}' is registered by more than one ISkillRegistry.");
                }
                _toolToRegistry[tool.Name] = registry;
            }
        }
    }

    /// <inheritdoc />
    public string? Endpoint { get; }

    /// <inheritdoc />
    public McpSession IssueSession(
        string agentId,
        string threadId,
        string callerKind = "agent",
        Guid messageId = default)
    {
        var subject = MaterialiseSubject(agentId, callerKind);
        var token = GenerateToken();
        var session = new McpSession(token, agentId, threadId, callerKind, subject, messageId);
        _sessions[token] = session;
        return session;
    }

    /// <summary>
    /// Builds the <see cref="McpSession.Subject"/> address from the
    /// IssueSession parameters. Every production caller
    /// (A2AExecutionDispatcher, PersistentAgentLifecycle) already hands in
    /// a canonical-Guid agentId and a scheme-shaped callerKind, so this
    /// is the contract — not a best-effort projection. Fails fast when
    /// the inputs cannot produce a subject so the effective-grant gate
    /// (#2379) applies uniformly to every session; there is no fail-open
    /// path that would let an unauthenticated session bypass enforcement.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="agentId"/> is not Guid-shaped or
    /// <paramref name="callerKind"/> is not <see cref="Address.AgentScheme"/>
    /// / <see cref="Address.UnitScheme"/>.
    /// </exception>
    private static Address MaterialiseSubject(string agentId, string callerKind)
    {
        if (!GuidFormatter.TryParse(agentId, out var id))
        {
            throw new ArgumentException(
                $"MCP session agentId '{agentId}' is not a Guid; every session must " +
                "bind to an Address-shaped subject so the effective-grant gate can " +
                "evaluate it (#2379). Pass the canonical 32-char no-dash id or a " +
                "dashed Guid.",
                nameof(agentId));
        }

        if (!string.Equals(callerKind, Address.AgentScheme, StringComparison.Ordinal)
            && !string.Equals(callerKind, Address.UnitScheme, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"MCP session callerKind '{callerKind}' is not a subject scheme; " +
                $"pass '{Address.AgentScheme}' or '{Address.UnitScheme}' so the " +
                "session's Subject is routable through IToolGrantResolver (#2379).",
                nameof(callerKind));
        }

        return new Address(callerKind, id);
    }

    /// <inheritdoc />
    public void RevokeSession(string token) => _sessions.TryRemove(token, out _);

    /// <summary>
    /// Serves one MCP JSON-RPC request. Mapped as a minimal-API route on the
    /// worker's Kestrel host (ADR-0052 / #2625) — the route reads the request
    /// body and <c>Authorization</c> header from <see cref="HttpContext"/> and
    /// writes the JSON-RPC response back onto <see cref="HttpContext.Response"/>.
    /// </summary>
    public async Task HandleRequestAsync(HttpContext context, CancellationToken ct)
    {
        var request = context.Request;
        var response = context.Response;

        if (!HttpMethods.IsPost(request.Method))
        {
            response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
            return;
        }

        var token = ExtractBearerToken(request.Headers.Authorization);
        if (token is null || !_sessions.TryGetValue(token, out var session))
        {
            await WriteErrorAsync(
                response, null, McpRpcErrorCodes.Unauthorized, "Missing or invalid bearer token.");
            return;
        }

        McpRpcRequest? rpcRequest;
        try
        {
            using var reader = new StreamReader(request.Body, Encoding.UTF8);
            var body = await reader.ReadToEndAsync(ct);
            rpcRequest = JsonSerializer.Deserialize<McpRpcRequest>(body);
        }
        catch (JsonException ex)
        {
            await WriteErrorAsync(response, null, McpRpcErrorCodes.ParseError, ex.Message);
            return;
        }

        if (rpcRequest is null || string.IsNullOrEmpty(rpcRequest.Method))
        {
            await WriteErrorAsync(response, null, McpRpcErrorCodes.InvalidRequest, "Empty or malformed request.");
            return;
        }

        try
        {
            switch (rpcRequest.Method)
            {
                case "initialize":
                    await WriteResultAsync(response, rpcRequest.Id, BuildInitializeResult(session));
                    return;

                case "tools/list":
                    await WriteResultAsync(response, rpcRequest.Id, await BuildToolListResultAsync(session, ct));
                    return;

                case "tools/call":
                    await HandleToolCallAsync(response, rpcRequest, session, ct);
                    return;

                default:
                    await WriteErrorAsync(
                        response, rpcRequest.Id, McpRpcErrorCodes.MethodNotFound,
                        $"Method '{rpcRequest.Method}' is not supported.");
                    return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MCP request failed: method={Method}", rpcRequest.Method);
            await WriteErrorAsync(response, rpcRequest.Id, McpRpcErrorCodes.InternalError, ex.Message);
        }
    }

    private async Task HandleToolCallAsync(
        HttpResponse response,
        McpRpcRequest request,
        McpSession session,
        CancellationToken ct)
    {
        if (request.Params is not { ValueKind: JsonValueKind.Object } paramsElement)
        {
            await WriteErrorAsync(
                response, request.Id, McpRpcErrorCodes.InvalidParams,
                "tools/call requires a params object.");
            return;
        }

        if (!paramsElement.TryGetProperty("name", out var nameProp) ||
            nameProp.ValueKind != JsonValueKind.String)
        {
            await WriteErrorAsync(
                response, request.Id, McpRpcErrorCodes.InvalidParams,
                "tools/call requires a 'name' string.");
            return;
        }

        var toolName = nameProp.GetString()!;
        var arguments = paramsElement.TryGetProperty("arguments", out var argsProp) &&
                        argsProp.ValueKind == JsonValueKind.Object
            ? argsProp
            : JsonSerializer.SerializeToElement(new { });

        if (!_toolToRegistry.TryGetValue(toolName, out var registry))
        {
            await WriteErrorAsync(
                response, request.Id, McpRpcErrorCodes.MethodNotFound,
                $"Tool '{toolName}' is not registered.");
            return;
        }

        _logger.LogInformation(
            "MCP tools/call: {Tool} (agent={AgentId} thread={ThreadId})",
            toolName, session.AgentId, session.ThreadId);

        // Effective-grant gate (#2379). Registration says "the platform
        // knows this tool"; the resolver says "this subject may call it".
        // Reject ungranted tools before consulting the unit-policy
        // enforcer so the policy gate sees only tools the subject was
        // entitled to in the first place. The gate applies uniformly to
        // every session — IssueSession guarantees a materialised Subject,
        // so the only bypass is "no resolver registered" (limited test
        // harnesses), in which case TryGetEffectiveGrantsAsync returns
        // null and the gate falls through to unit policy.
        var grants = await TryGetEffectiveGrantsAsync(session, ct);
        if (grants is not null && !grants.Contains(toolName))
        {
            _logger.LogWarning(
                "MCP tools/call rejected: tool {Tool} is not in the effective grant set for {Subject}.",
                toolName, session.Subject);
            await EmitToolFailureActivityAsync(
                session,
                toolName,
                ActivitySeverity.Warning,
                $"Tool '{toolName}' is not in the effective grant set for {session.Subject}.",
                reason: "ungranted",
                ct: ct);
            await WriteErrorAsync(
                response, request.Id, McpRpcErrorCodes.ToolNotGranted,
                $"Tool '{toolName}' is not granted to this session.");
            return;
        }

        // Unit-policy enforcement (#162 / #163). Every skill invocation
        // routes through IUnitPolicyEnforcer — if any unit the agent belongs
        // to blocks this tool, the call never reaches the registry and the
        // model sees a tool error so it can self-correct. The enforcer is
        // resolved from a fresh scope because the default implementation
        // depends on scoped repositories; when no scope factory is wired
        // (unit tests that build the server standalone), enforcement is
        // skipped — production hosts always supply one via DI.
        var denial = await TryEvaluateSkillPolicyAsync(session, toolName, ct);
        if (denial is not null)
        {
            _logger.LogWarning(
                "MCP tools/call denied by unit policy: {Tool} (agent={AgentId} unit={UnitId}) — {Reason}",
                toolName, session.AgentId, denial.Value.DenyingUnitId, denial.Value.Reason);
            await EmitToolFailureActivityAsync(
                session,
                toolName,
                ActivitySeverity.Warning,
                $"Tool '{toolName}' denied by unit policy: {denial.Value.Reason ?? "no reason given"}",
                reason: denial.Value.Reason,
                deniedByUnitId: denial.Value.DenyingUnitId,
                ct: ct);
            await WriteResultAsync(response, request.Id, new
            {
                content = new[]
                {
                    new
                    {
                        type = "text",
                        text = denial.Value.Reason ?? "Skill denied by unit policy.",
                    },
                },
                isError = true,
            });
            return;
        }

        try
        {
            // Thread caller identity into the registry via the rich
            // overload (#2231). Skills that don't override the new method
            // fall back to the original no-context behaviour through the
            // default interface-method delegation.
            var callContext = new ToolCallContext(
                CallerId: session.AgentId,
                CallerKind: session.CallerKind,
                ThreadId: session.ThreadId,
                MessageId: session.MessageId);
            var result = await registry.InvokeAsync(toolName, arguments, callContext, ct);
            await WriteResultAsync(response, request.Id, new
            {
                content = new[]
                {
                    new
                    {
                        type = "text",
                        text = result.GetRawText()
                    }
                },
                isError = false
            });
        }
        catch (SkillNotFoundException ex)
        {
            await EmitToolFailureActivityAsync(
                session,
                toolName,
                ActivitySeverity.Warning,
                $"Tool '{toolName}' not found",
                exception: ex,
                ct: ct);
            await WriteErrorAsync(response, request.Id, McpRpcErrorCodes.MethodNotFound, ex.Message);
        }
        catch (ArgumentException ex)
        {
            // Malformed arguments are surfaced to the model as a tool error so it can
            // self-correct, rather than as a JSON-RPC transport error. Server-side
            // details are logged so operators can still audit rejected calls.
            _logger.LogWarning(ex,
                "MCP tool {Tool} rejected malformed arguments (agent={AgentId} thread={ThreadId})",
                toolName, session.AgentId, session.ThreadId);
            await EmitToolFailureActivityAsync(
                session,
                toolName,
                ActivitySeverity.Warning,
                $"Tool '{toolName}' rejected arguments: {ex.Message}",
                exception: ex,
                ct: ct);
            await WriteResultAsync(response, request.Id, new
            {
                content = new[]
                {
                    new
                    {
                        type = "text",
                        text = $"Invalid tool arguments: {ex.Message}"
                    }
                },
                isError = true
            });
        }
        catch (Exception ex)
        {
            // Execution failures (HTTP 4xx/5xx from GitHub, timeouts, etc.) must surface to
            // the model with isError so the loop can decide what to do. The exception is
            // fully logged server-side per #105 — we never swallow silently.
            _logger.LogError(ex,
                "MCP tool {Tool} threw while executing (agent={AgentId} thread={ThreadId})",
                toolName, session.AgentId, session.ThreadId);
            // Surface the failure on the activity bus too — without this, operators see
            // tool failures (e.g. unconfigured GitHub installation id) only in the worker
            // ILogger sink and the activity feed stays silent. Severity is Error so
            // `Severity >= Warning` filters still match and operators can distinguish
            // unexpected exceptions from arg / policy denials (both Warning above).
            await EmitToolFailureActivityAsync(
                session,
                toolName,
                ActivitySeverity.Error,
                $"Tool '{toolName}' failed: {ex.Message}",
                exception: ex,
                ct: ct);
            await WriteResultAsync(response, request.Id, new
            {
                content = new[]
                {
                    new
                    {
                        type = "text",
                        text = $"Tool '{toolName}' failed: {ex.Message}"
                    }
                },
                isError = true
            });
        }
    }

    /// <summary>
    /// Publishes a <see cref="ActivityEventType.ToolResult"/> activity event when a tool
    /// call fails — either because policy denied it, the registry rejected the call,
    /// the arguments were malformed, or the underlying skill threw. The MCP layer was
    /// previously logging these failures only to <see cref="ILogger"/>, which left the
    /// portal's activity feed silent for the most common operator-visible failures.
    /// Wiring emission here means the operator sees the failure with the exception
    /// message attached, paired with the matching
    /// <see cref="ActivityEventType.ToolCall"/> via the agent-id source field.
    /// </summary>
    private async Task EmitToolFailureActivityAsync(
        McpSession session,
        string toolName,
        ActivitySeverity severity,
        string summary,
        Exception? exception = null,
        string? reason = null,
        string? deniedByUnitId = null,
        CancellationToken ct = default)
    {
        var bus = _activityEventBus;
        if (bus is null)
        {
            return;
        }

        try
        {
            var details = JsonSerializer.SerializeToElement(new
            {
                toolName,
                threadId = session.ThreadId,
                callerKind = session.CallerKind,
                exceptionType = exception?.GetType().FullName,
                exceptionMessage = exception?.Message,
                reason,
                deniedByUnitId,
            });

            // IssueSession guarantees Subject is always materialised from a
            // Guid-shaped agent id, so we can route the activity event off
            // the session subject directly — no defensive Guid-parse needed.
            var activityEvent = new ActivityEvent(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                session.Subject,
                ActivityEventType.ToolResult,
                severity,
                summary,
                details,
                CorrelationId: session.ThreadId);

            await bus.PublishAsync(activityEvent, ct);
        }
        catch (Exception emitEx)
        {
            // The bus is a fire-and-forget audit sink — failing to publish must never
            // turn a tool error (already on its way back to the model) into a hard
            // server fault. Log and swallow, matching the AgentActor.EmitActivityEventAsync
            // pattern in src/Cvoya.Spring.Dapr/Actors/AgentActor.cs.
            _logger.LogWarning(emitEx,
                "Failed to publish ToolResult activity event for {Tool} (agent={AgentId}); " +
                "the tool error is still being returned to the caller.",
                toolName, session.AgentId);
        }
    }

    /// <summary>
    /// Consults <see cref="IUnitPolicyEnforcer"/> for a skill invocation and
    /// returns a <see cref="PolicyDecision"/> when the call must be denied,
    /// or <c>null</c> when the call is allowed. A missing scope factory or
    /// a missing enforcer registration (test harnesses) is treated as
    /// "no policy applies" so existing skill-invocation tests keep passing.
    /// Enforcer failures are logged and treated the same way — policy
    /// infrastructure must never convert a routine tool call into a hard
    /// error for the model.
    /// </summary>
    private async Task<PolicyDecision?> TryEvaluateSkillPolicyAsync(
        McpSession session, string toolName, CancellationToken ct)
    {
        if (_scopeFactory is null)
        {
            return null;
        }

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var enforcer = scope.ServiceProvider.GetService<IUnitPolicyEnforcer>();
            if (enforcer is null)
            {
                return null;
            }

            var decision = await enforcer.EvaluateSkillInvocationAsync(
                session.AgentId, toolName, ct);

            return decision.IsAllowed ? null : decision;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Policy enforcer threw while evaluating {Tool} for agent {AgentId}; allowing the call.",
                toolName, session.AgentId);
            return null;
        }
    }

    private object BuildInitializeResult(McpSession session)
    {
        return new
        {
            protocolVersion = "2024-11-05",
            serverInfo = new { name = "spring-voyage-mcp", version = "1.0.0" },
            capabilities = new
            {
                tools = new { }
            },
            // Expose the session binding so the client can confirm attribution.
            meta = new
            {
                agentId = session.AgentId,
                threadId = session.ThreadId
            }
        };
    }

    /// <summary>
    /// Builds the <c>tools/list</c> result, scoped to the active session's
    /// effective tool grants (#2379). Registration tells the server "this
    /// tool exists"; the resolver tells it "this subject may see it" —
    /// callers MUST NOT discover tools they aren't allowed to invoke. The
    /// resolver is the single source of truth across all four provenance
    /// tiers (platform / connector / image / explicit), so we intersect
    /// the registered set against its result rather than re-implementing
    /// tier-specific filters here. When the host doesn't register an
    /// <see cref="IToolGrantResolver"/> (limited unit-test harnesses),
    /// the unfiltered list surfaces — production composition always
    /// supplies one via <c>AddCvoyaSpringDapr</c>.
    /// </summary>
    private async Task<object> BuildToolListResultAsync(McpSession session, CancellationToken ct)
    {
        var allDefinitions = _registries.SelectMany(r => r.GetToolDefinitions());

        var grants = await TryGetEffectiveGrantsAsync(session, ct);
        var filtered = grants is null
            ? allDefinitions
            : allDefinitions.Where(t => grants.Contains(t.Name));

        var tools = filtered
            .Select(t => new
            {
                name = t.Name,
                description = t.Description,
                inputSchema = t.InputSchema,
            })
            .ToArray();

        return new { tools };
    }

    /// <summary>
    /// Returns the canonical names of every tool effectively granted to
    /// the session's subject, or <c>null</c> when no resolver is wired
    /// (limited unit-test harnesses). Production hosts (AddCvoyaSpringDapr)
    /// always register a real resolver, so the null branch is only
    /// reachable in tests today. The session's <see cref="McpSession.Subject"/>
    /// is always present — <see cref="IssueSession"/> enforces that at
    /// session-establishment time, so the resolver always sees a real
    /// Address. Resolver exceptions propagate; converting them into a
    /// silent allow-all would let a transient datastore outage open the
    /// gate for every caller and is exactly the fail-open the #2379
    /// review rejected.
    /// </summary>
    private async Task<HashSet<string>?> TryGetEffectiveGrantsAsync(
        McpSession session, CancellationToken ct)
    {
        if (_scopeFactory is null)
        {
            return null;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var resolver = scope.ServiceProvider.GetService<IToolGrantResolver>();
        if (resolver is null)
        {
            return null;
        }

        var effective = await resolver.ResolveAsync(session.Subject, ct);
        return new HashSet<string>(
            effective.Select(t => t.Name),
            StringComparer.Ordinal);
    }

    private static async Task WriteResultAsync(
        HttpResponse response, JsonElement? id, object result)
    {
        var payload = new McpRpcResponse { Id = id, Result = result };
        await WriteJsonAsync(response, (int)HttpStatusCode.OK, payload);
    }

    private static async Task WriteErrorAsync(
        HttpResponse response, JsonElement? id, int code, string message)
    {
        var payload = new McpRpcErrorResponse
        {
            Id = id,
            Error = new McpRpcError { Code = code, Message = message }
        };

        var status = code == McpRpcErrorCodes.Unauthorized
            ? (int)HttpStatusCode.Unauthorized
            : (int)HttpStatusCode.OK; // JSON-RPC errors are transport-OK.

        await WriteJsonAsync(response, status, payload);
    }

    private static async Task WriteJsonAsync(HttpResponse response, int status, object body)
    {
        response.StatusCode = status;
        response.ContentType = "application/json";
        var buffer = JsonSerializer.SerializeToUtf8Bytes(body);
        response.ContentLength = buffer.Length;
        await response.Body.WriteAsync(buffer);
    }

    private static string? ExtractBearerToken(string? authHeader)
    {
        if (string.IsNullOrWhiteSpace(authHeader))
        {
            return null;
        }
        const string prefix = "Bearer ";
        return authHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? authHeader[prefix.Length..].Trim()
            : null;
    }

    private static string GenerateToken()
    {
        Span<byte> buffer = stackalloc byte[32];
        RandomNumberGenerator.Fill(buffer);
        return Convert.ToHexStringLower(buffer);
    }
}
