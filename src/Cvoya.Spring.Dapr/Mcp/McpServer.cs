// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Mcp;

using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Policies;
using Cvoya.Spring.Core.Skills;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// In-process MCP server that exposes <see cref="ISkillRegistry"/> tools to
/// containerised agents over loopback HTTP + JSON-RPC 2.0. Implements the
/// minimum subset of MCP needed to exchange tool calls: <c>initialize</c>,
/// <c>tools/list</c>, and <c>tools/call</c>. Streaming and notifications are
/// intentionally out of scope — GitHub connector calls are short RPCs.
/// </summary>
/// <remarks>
/// Auth model: the dispatcher calls <see cref="IssueSession"/> before launching
/// a container and hands the resulting bearer token to the container via an env
/// var. The server validates the <c>Authorization: Bearer &lt;token&gt;</c>
/// header on every request and binds the call to the issued
/// <see cref="McpSession"/>. Tokens are single-agent/single-conversation and
/// revoked when the invocation completes.
/// </remarks>
public class McpServer : IMcpServer, IHostedService, IDisposable
{
    private readonly IReadOnlyList<ISkillRegistry> _registries;
    private readonly Dictionary<string, ISkillRegistry> _toolToRegistry;
    private readonly McpServerOptions _options;
    private readonly ILogger _logger;
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly ConcurrentDictionary<string, McpSession> _sessions = new(StringComparer.Ordinal);

    private HttpListener? _listener;
    private CancellationTokenSource? _acceptCts;
    private Task? _acceptLoop;
    private int _boundPort;

    /// <summary>
    /// Initializes the server with the set of registries to expose. The server
    /// does not start until <see cref="StartAsync"/> is invoked by the host.
    /// </summary>
    /// <remarks>
    /// <paramref name="scopeFactory"/> is optional so standalone / test
    /// constructions can continue to instantiate the server directly. When
    /// it is supplied, every <c>tools/call</c> resolves an
    /// <see cref="IUnitPolicyEnforcer"/> from a fresh scope and consults the
    /// unit-policy framework (#162) before dispatching to the underlying
    /// <see cref="ISkillRegistry"/>. Denials are surfaced to the model as a
    /// tool error (isError=true) so the agent's conversation can see the
    /// block and adapt.
    /// </remarks>
    public McpServer(
        IEnumerable<ISkillRegistry> registries,
        IOptions<McpServerOptions> options,
        ILoggerFactory loggerFactory,
        IServiceScopeFactory? scopeFactory = null)
    {
        _registries = registries.ToList();
        _options = options.Value;
        _logger = loggerFactory.CreateLogger<McpServer>();
        _scopeFactory = scopeFactory;

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
    public string? Endpoint { get; private set; }

    /// <inheritdoc />
    public McpSession IssueSession(string agentId, string conversationId)
    {
        var token = GenerateToken();
        var session = new McpSession(token, agentId, conversationId);
        _sessions[token] = session;
        return session;
    }

    /// <inheritdoc />
    public void RevokeSession(string token) => _sessions.TryRemove(token, out _);

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var port = _options.Port;
        if (port == 0)
        {
            port = PickFreePort();
        }

        _boundPort = port;
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/mcp/");
        _listener.Start();

        Endpoint = $"http://{_options.ContainerHost}:{port}/mcp/";

        _acceptCts = new CancellationTokenSource();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_acceptCts.Token));

        _logger.LogInformation(
            "MCP server listening on 127.0.0.1:{Port}; container endpoint {Endpoint}",
            port, Endpoint);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            _acceptCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Provider disposed before host StopAsync (e.g. WebApplicationFactory
            // disposes the TestServer before stopping the host). Accept loop has
            // already been torn down as part of Dispose().
        }

        try
        {
            _listener?.Stop();
        }
        catch (ObjectDisposedException)
        {
            // Listener already disposed; nothing to do.
        }

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Shutdown deadline reached — accept loop will exit when listener stops.
            }
        }

        _logger.LogInformation("MCP server stopped.");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _acceptCts?.Dispose();
        (_listener as IDisposable)?.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is { IsListening: true })
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (HttpListenerException)
            {
                // Listener was stopped.
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await HandleRequestAsync(context, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unhandled exception while serving MCP request.");
                    try { context.Response.Close(); } catch { /* already closed */ }
                }
            }, ct);
        }
    }

    internal async Task HandleRequestAsync(HttpListenerContext context, CancellationToken ct)
    {
        var request = context.Request;
        var response = context.Response;

        if (!string.Equals(request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
        {
            response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
            response.Close();
            return;
        }

        var token = ExtractBearerToken(request.Headers["Authorization"]);
        if (token is null || !_sessions.TryGetValue(token, out var session))
        {
            await WriteErrorAsync(
                response, null, McpRpcErrorCodes.Unauthorized, "Missing or invalid bearer token.");
            return;
        }

        McpRpcRequest? rpcRequest;
        try
        {
            using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
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
                    await WriteResultAsync(response, rpcRequest.Id, BuildToolListResult());
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
        HttpListenerResponse response,
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
            "MCP tools/call: {Tool} (agent={AgentId} conv={ConversationId})",
            toolName, session.AgentId, session.ConversationId);

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
            var result = await registry.InvokeAsync(toolName, arguments, ct);
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
            await WriteErrorAsync(response, request.Id, McpRpcErrorCodes.MethodNotFound, ex.Message);
        }
        catch (ArgumentException ex)
        {
            // Malformed arguments are surfaced to the model as a tool error so it can
            // self-correct, rather than as a JSON-RPC transport error. Server-side
            // details are logged so operators can still audit rejected calls.
            _logger.LogWarning(ex,
                "MCP tool {Tool} rejected malformed arguments (agent={AgentId} conv={ConversationId})",
                toolName, session.AgentId, session.ConversationId);
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
                "MCP tool {Tool} threw while executing (agent={AgentId} conv={ConversationId})",
                toolName, session.AgentId, session.ConversationId);
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
            serverInfo = new { name = "spring-voyage-mcp", version = "0.1.0" },
            capabilities = new
            {
                tools = new { }
            },
            // Expose the session binding so the client can confirm attribution.
            meta = new
            {
                agentId = session.AgentId,
                conversationId = session.ConversationId
            }
        };
    }

    private object BuildToolListResult()
    {
        var tools = _registries
            .SelectMany(r => r.GetToolDefinitions())
            .Select(t => new
            {
                name = t.Name,
                description = t.Description,
                inputSchema = t.InputSchema
            })
            .ToArray();

        return new { tools };
    }

    private static async Task WriteResultAsync(
        HttpListenerResponse response, JsonElement? id, object result)
    {
        var payload = new McpRpcResponse { Id = id, Result = result };
        await WriteJsonAsync(response, (int)HttpStatusCode.OK, payload);
    }

    private static async Task WriteErrorAsync(
        HttpListenerResponse response, JsonElement? id, int code, string message)
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

    private static async Task WriteJsonAsync(HttpListenerResponse response, int status, object body)
    {
        response.StatusCode = status;
        response.ContentType = "application/json";
        var buffer = JsonSerializer.SerializeToUtf8Bytes(body);
        response.ContentLength64 = buffer.Length;
        await response.OutputStream.WriteAsync(buffer);
        response.OutputStream.Close();
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

    private static int PickFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}