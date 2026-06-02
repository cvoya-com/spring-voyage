// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// HttpClient-backed <see cref="IAgentToolsIntrospector"/>. Fetches
/// <c>GET /a2a/tools</c> from the agent's listener at deploy /
/// image-rotation time and caches the result on
/// <c>agent_definitions.image_tools</c> or
/// <c>unit_definitions.image_tools</c> (units-are-agents per ADR-0039).
/// </summary>
/// <remarks>
/// <para>
/// Sub C (#2336) of the Tools wave (#2332). The dispatcher invokes the
/// introspector from
/// <see cref="PersistentAgentLifecycle.DeployAsync"/> after the readiness
/// probe lands, so the call is guaranteed to hit a live HTTP listener.
/// </para>
/// <para>
/// Failure semantics (#3003): the fetch is retried a few times with backoff
/// (a relaunched agent's sidecar may not be listening for the first few
/// hundred milliseconds). A non-200, timeout, transport error, or parse
/// failure that persists across all attempts logs a warning and <b>preserves
/// the previously-cached <c>image_tools</c></b> rather than overwriting it
/// with an empty array — so a transient race never drops a live agent's real
/// tool list. A <i>successful</i> fetch that returns an empty array (a
/// legitimately tool-less image) is authoritative and does overwrite the
/// cache. Either way the deploy still succeeds — introspection is never
/// load-bearing on boot.
/// </para>
/// <para>
/// The implementation walks both <c>agent_definitions</c> and
/// <c>unit_definitions</c> to find the row matching
/// <paramref name="agentId"/>: the deploy path doesn't tell the
/// introspector which kind of entity it's persisting onto, and the row
/// kinds are mutually exclusive by Guid. A unit always lives in
/// <c>unit_definitions</c>; an agent in <c>agent_definitions</c>. If
/// neither matches the introspector logs at <c>Information</c> and
/// returns the parsed array without persisting (the deploy is still
/// useful even when the persistence row hasn't yet been created — e.g.
/// the install-pipeline ordering edge).
/// </para>
/// </remarks>
public sealed class HttpAgentToolsIntrospector : IAgentToolsIntrospector
{
    /// <summary>
    /// Endpoint path the introspector calls. Matches
    /// <c>Cvoya.Spring.AgentSdk.ToolsEndpointServer.ToolsPath</c> on the
    /// agent side, and the <c>GET /a2a/tools</c> route the sidecar serves.
    /// </summary>
    public const string ToolsPath = "a2a/tools";

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Number of <c>GET /a2a/tools</c> attempts before giving up and keeping
    /// the previously-cached <c>image_tools</c> (#3003). On (re)launch the
    /// sidecar may not be listening yet — the first fetch hits a connection
    /// refusal milliseconds before the listener binds — so a single attempt
    /// would persist an empty array and drop the agent's real tool list.
    /// </summary>
    private const int MaxFetchAttempts = 3;

    /// <summary>
    /// Backoff before the first retry; doubled on each subsequent attempt.
    /// Kept short — the failure mode this covers is a not-yet-bound listener,
    /// which resolves within a few hundred milliseconds of the readiness probe.
    /// </summary>
    private static readonly TimeSpan InitialRetryBackoff = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Outcome of <see cref="FetchToolsAsync"/>: distinguishes a successful
    /// fetch (a clean 200 + valid JSON, possibly an empty array — a
    /// legitimately tool-less image) from a failed fetch (transport error,
    /// timeout, non-2xx, or invalid JSON, after retries). Only a successful
    /// fetch is persisted; a failed fetch preserves the prior cache (#3003).
    /// </summary>
    private readonly record struct FetchResult(bool Fetched, IReadOnlyList<ToolDefinition> Tools)
    {
        public static readonly FetchResult Failed = new(false, Array.Empty<ToolDefinition>());

        public static FetchResult Success(IReadOnlyList<ToolDefinition> tools) => new(true, tools);
    }

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>HttpClient name used when the production
    /// <see cref="IHttpClientFactory"/> path is taken. Tests inject a
    /// direct factory via the alternate constructor and bypass the
    /// factory entirely.</summary>
    public const string HttpClientName = nameof(HttpAgentToolsIntrospector);

    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly Func<HttpClient>? _httpClientFactoryOverride;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<HttpAgentToolsIntrospector> _logger;

    /// <summary>Constructs an introspector using the supplied factories.</summary>
    public HttpAgentToolsIntrospector(
        IHttpClientFactory httpClientFactory,
        IServiceScopeFactory scopeFactory,
        ILogger<HttpAgentToolsIntrospector> logger)
    {
        _httpClientFactory = httpClientFactory
            ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _scopeFactory = scopeFactory
            ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger
            ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Test-only constructor that injects an explicit HttpClient factory.
    /// The production path uses <see cref="IHttpClientFactory"/>; tests
    /// that stand up an in-process listener pass <see cref="HttpClient"/>
    /// directly.
    /// </summary>
    public HttpAgentToolsIntrospector(
        Func<HttpClient> httpClientFactory,
        IServiceScopeFactory scopeFactory,
        ILogger<HttpAgentToolsIntrospector> logger)
    {
        _httpClientFactoryOverride = httpClientFactory
            ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _scopeFactory = scopeFactory
            ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger
            ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ToolDefinition>> IntrospectAndPersistAsync(
        Guid agentId,
        string containerId,
        Uri endpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        var toolsUri = new Uri(endpoint, ToolsPath);
        var result = await FetchToolsAsync(toolsUri, cancellationToken).ConfigureAwait(false);

        if (!result.Fetched)
        {
            // #3003: the fetch failed (transport error, timeout, non-2xx, or
            // invalid JSON — after retries). Do NOT persist: overwriting
            // image_tools with an empty array here would drop a relaunched
            // agent's real tool list on a transient not-yet-listening race.
            // Preserve whatever was last cached; the next successful
            // introspection (deploy / image rotation) refreshes it.
            _logger.LogWarning(
                "Agent /a2a/tools at {Uri} could not be introspected after {Attempts} attempt(s); " +
                "preserving the previously-cached image_tools for {AgentId} (skipping persist).",
                toolsUri, MaxFetchAttempts, agentId);
            return Array.Empty<ToolDefinition>();
        }

        // A successful fetch — even an empty array (a legitimately tool-less
        // image) — is authoritative and overwrites the cache.
        await PersistAsync(agentId, result.Tools, cancellationToken).ConfigureAwait(false);
        return result.Tools;
    }

    private async Task<FetchResult> FetchToolsAsync(
        Uri toolsUri,
        CancellationToken cancellationToken)
    {
        var backoff = InitialRetryBackoff;
        for (var attempt = 1; ; attempt++)
        {
            var result = await TryFetchOnceAsync(toolsUri, attempt, cancellationToken).ConfigureAwait(false);
            if (result.Fetched)
            {
                return result;
            }

            if (attempt >= MaxFetchAttempts)
            {
                return FetchResult.Failed;
            }

            // Caller cancellation propagates out of Task.Delay (it is the only
            // token here); a not-yet-bound listener resolves within the backoff.
            await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);
            backoff += backoff;
        }
    }

    private async Task<FetchResult> TryFetchOnceAsync(
        Uri toolsUri,
        int attempt,
        CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(DefaultTimeout);

        try
        {
            using var client = AcquireClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, toolsUri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await client.SendAsync(request, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug(
                    "Agent /a2a/tools at {Uri} returned non-success status {Status} (attempt {Attempt}/{Max}).",
                    toolsUri, (int)response.StatusCode, attempt, MaxFetchAttempts);
                return FetchResult.Failed;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            return ParseFetchedTools(stream, toolsUri, attempt);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Caller-driven cancellation — propagate.
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug(
                "Agent /a2a/tools at {Uri} timed out after {Timeout} (attempt {Attempt}/{Max}).",
                toolsUri, DefaultTimeout, attempt, MaxFetchAttempts);
            return FetchResult.Failed;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "Agent /a2a/tools at {Uri} failed (attempt {Attempt}/{Max}): {Message}.",
                toolsUri, attempt, MaxFetchAttempts, ex.Message);
            return FetchResult.Failed;
        }
    }

    private FetchResult ParseFetchedTools(System.IO.Stream stream, Uri toolsUri, int attempt)
    {
        try
        {
            using var document = JsonDocument.Parse(stream);
            return FetchResult.Success(ParseTools(document.RootElement));
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(
                ex,
                "Agent /a2a/tools at {Uri} produced invalid JSON (attempt {Attempt}/{Max}).",
                toolsUri, attempt, MaxFetchAttempts);
            return FetchResult.Failed;
        }
    }

    internal static IReadOnlyList<ToolDefinition> ParseTools(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<ToolDefinition>();
        }

        var tools = new List<ToolDefinition>(root.GetArrayLength());
        foreach (var element in root.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            if (!element.TryGetProperty("name", out var nameProp) ||
                nameProp.ValueKind != JsonValueKind.String)
            {
                continue;
            }
            var name = nameProp.GetString();
            if (string.IsNullOrEmpty(name) || !ToolNaming.IsValid(name))
            {
                continue;
            }

            var description = element.TryGetProperty("description", out var descProp) &&
                descProp.ValueKind == JsonValueKind.String
                ? descProp.GetString() ?? string.Empty
                : string.Empty;

            JsonElement inputSchema;
            if (element.TryGetProperty("inputSchema", out var schemaProp))
            {
                inputSchema = schemaProp.Clone();
            }
            else
            {
                // Empty object — keeps the wire shape stable when an agent
                // forgets the field. The grant resolver doesn't validate the
                // schema body, so the empty-object default is benign.
                inputSchema = JsonDocument.Parse("{}").RootElement.Clone();
            }

            // Third-party tools discovered from a remote agent's /tools
            // endpoint genuinely have no SV category. Pass an empty
            // category — they remain reachable through the flat
            // tools/list surface but are not enumerated by the
            // category-aware discovery tools.
            tools.Add(new ToolDefinition(name, description, inputSchema, string.Empty));
        }
        return tools;
    }

    /// <summary>
    /// Persists <paramref name="tools"/> onto
    /// <c>agent_definitions.image_tools</c> or
    /// <c>unit_definitions.image_tools</c>, whichever row matches
    /// <paramref name="agentId"/>.
    /// </summary>
    public async Task PersistAsync(
        Guid agentId,
        IReadOnlyList<ToolDefinition> tools,
        CancellationToken cancellationToken)
    {
        var serialised = SerialiseToJsonElement(tools);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        // ADR-0039: units-are-agents — try both tables; a Guid only ever
        // resolves on one (the row kinds are mutually exclusive).
        var agentRow = await db.AgentDefinitions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(a => a.Id == agentId, cancellationToken)
            .ConfigureAwait(false);
        if (agentRow is not null)
        {
            agentRow.ImageTools = serialised;
            agentRow.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Cached image_tools[{Count}] on agent_definitions row {AgentId}.",
                tools.Count, agentId);
            return;
        }

        var unitRow = await db.UnitDefinitions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == agentId, cancellationToken)
            .ConfigureAwait(false);
        if (unitRow is not null)
        {
            unitRow.ImageTools = serialised;
            unitRow.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Cached image_tools[{Count}] on unit_definitions row {AgentId}.",
                tools.Count, agentId);
            return;
        }

        _logger.LogInformation(
            "No agent_definitions or unit_definitions row found for {AgentId}; skipping image_tools cache update.",
            agentId);
    }

    /// <summary>
    /// Serialises a list of <see cref="ToolDefinition"/>s to the wire JSON
    /// element the <c>image_tools</c> column stores. Visible for testing.
    /// </summary>
    public static JsonElement SerialiseToJsonElement(IReadOnlyList<ToolDefinition> tools)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            tools,
            ImageToolsWriteOptions);
        using var doc = JsonDocument.Parse(bytes);
        return doc.RootElement.Clone();
    }

    internal static readonly JsonSerializerOptions ImageToolsWriteOptions = BuildWriteOptions();

    private static JsonSerializerOptions BuildWriteOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
        options.Converters.Add(new ToolDefinitionWireConverter());
        return options;
    }

    private HttpClient AcquireClient()
    {
        if (_httpClientFactoryOverride is not null)
        {
            return _httpClientFactoryOverride();
        }
        return _httpClientFactory!.CreateClient(nameof(HttpAgentToolsIntrospector));
    }

    /// <summary>
    /// Wire-shape converter aligned with the SDK's emitter — same field
    /// set (<c>name</c> / <c>description</c> / <c>inputSchema</c>) and
    /// the <c>Namespace</c> computed property is excluded.
    /// </summary>
    private sealed class ToolDefinitionWireConverter : System.Text.Json.Serialization.JsonConverter<ToolDefinition>
    {
        public override ToolDefinition Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
            => throw new NotSupportedException(
                "HttpAgentToolsIntrospector serialises ToolDefinition for storage; " +
                "deserialisation goes through HttpAgentToolsIntrospector.ParseTools.");

        public override void Write(
            Utf8JsonWriter writer,
            ToolDefinition value,
            JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("name", value.Name);
            writer.WriteString("description", value.Description);
            writer.WritePropertyName("inputSchema");
            value.InputSchema.WriteTo(writer);
            writer.WriteEndObject();
        }
    }
}
