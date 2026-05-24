// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub;

using System.Globalization;
using System.Text.Json;

using Cvoya.Spring.Connector.GitHub.Auth;
using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Core.Units;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Connector-tier <see cref="ISkillRegistry"/> for the GitHub connector
/// (#2704). Exposes a single tool — <c>github.get_installation_token</c>
/// — that hands a calling agent the outbound bearer token its bound
/// installation authenticates with, so the model never has to guess at
/// an HTTP endpoint URL to fetch it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why only one tool.</b> Issues #2384 / #2383 locked in a deliberate
/// architectural choice: agents bound to a GitHub unit reach the upstream
/// API by running <c>gh</c> / <c>git</c> inside their container against
/// the credentials and identity env-vars stamped by
/// <see cref="GitHubConnectorRuntimeContextContributor"/>. There is no
/// <c>github.create_issue</c>, no <c>github.review_pr</c>, no shadow
/// shape that competes with the CLI. The token-fetch tool is the
/// narrowest possible exception: a read of platform-managed state the
/// model otherwise has to <em>fabricate a URL</em> to reach (#2704).
/// Adding any other <c>github.*</c> tool here re-opens the design
/// decision the regression test
/// <c>GitHubConnectorDoesNotRegisterMcpToolsTests</c> protects.
/// </para>
/// <para>
/// <b>Connector tier — not platform tier.</b> The tool sits in the
/// <c>github</c> namespace so the existing connector-grant pipeline
/// (#2335) automatically surfaces it on every agent whose owning-or-
/// ancestor unit binds GitHub, and only on those agents. A platform-tier
/// (<c>sv.*</c>) name would be enumerated on every agent in the system,
/// including ones that have no GitHub binding and would never need the
/// tool.
/// </para>
/// <para>
/// <b>Caller-aware.</b> The tool resolves the binding fresh on every
/// call by walking the caller's parent-unit chain (same shape as
/// <c>ToolGrantResolver.BuildAncestorChainAsync</c> uses to decide
/// grants). It does not cache the binding across calls because the
/// caller's effective binding can change between turns (a unit can
/// be rebound, an installation can be revoked); credentials minted
/// against a stale binding would surface as 401s downstream instead
/// of a clean "no GitHub binding" error.
/// </para>
/// </remarks>
public sealed class GitHubSkillRegistry : ISkillRegistry
{
    /// <summary>Tool name for <c>github.get_installation_token</c>.</summary>
    public const string GetInstallationTokenTool = "github.get_installation_token";

    private static readonly JsonElement GetInstallationTokenSchema = ParseSchema("""
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {}
        }
        """);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly GitHubBindingAuthResolver _authResolver;
    private readonly ILogger<GitHubSkillRegistry> _logger;
    private readonly IReadOnlyList<ToolDefinition> _tools;

    /// <summary>Creates the registry with its scoped-read dependencies.</summary>
    /// <param name="scopeFactory">Scope factory for per-call binding lookups.</param>
    /// <param name="authResolver">Single binding-auth dispatch (ADR-0047 §6).</param>
    /// <param name="loggerFactory">Logger factory for diagnostic logging.</param>
    public GitHubSkillRegistry(
        IServiceScopeFactory scopeFactory,
        GitHubBindingAuthResolver authResolver,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(authResolver);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _scopeFactory = scopeFactory;
        _authResolver = authResolver;
        _logger = loggerFactory.CreateLogger<GitHubSkillRegistry>();

        _tools = new[]
        {
            // Connector tools sit outside the SV category taxonomy; the
            // platform `sv.tools.list(category)` surface enumerates by
            // category and connector tools surface through the flat
            // tools/list path. Pass an empty category — matches the
            // ArxivSkillRegistry precedent.
            new ToolDefinition(
                GetInstallationTokenTool,
                "Return the outbound bearer token your unit's GitHub binding is currently " +
                "authenticated with, along with the kind of credential (App-installation " +
                "or PAT) and the server-stamped expiry when the App branch was used. Call " +
                "this when you need a token to hand to an HTTP client — do NOT construct " +
                "an HTTP request to fetch it from any platform URL; the platform does not " +
                "expose a token-fetch HTTP endpoint. The same value is also published " +
                "inside your container as the $SPRING_CONNECTOR_GITHUB_TOKEN env-var " +
                "(aliased as $GITHUB_TOKEN for gh / git), so most agents do not need this " +
                "tool at all — it exists as the canonical tool-call alternative for " +
                "runtimes that prefer a single dispatch shape.",
                GetInstallationTokenSchema,
                string.Empty),
        };
    }

    /// <inheritdoc />
    public string Name => "github";

    /// <inheritdoc />
    public IReadOnlyList<ToolDefinition> GetToolDefinitions() => _tools;

    /// <inheritdoc />
    public Task<JsonElement> InvokeAsync(string toolName, JsonElement arguments, CancellationToken cancellationToken = default) =>
        throw new SpringException(
            $"Tool '{toolName}' on the {Name} registry requires caller context. " +
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
            GetInstallationTokenTool => GetInstallationTokenAsync(context, cancellationToken),
            _ => throw new SkillNotFoundException(toolName),
        };
    }

    private async Task<JsonElement> GetInstallationTokenAsync(
        ToolCallContext context, CancellationToken cancellationToken)
    {
        var caller = ParseCaller(context);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var configStore = sp.GetRequiredService<IUnitConnectorConfigStore>();
        var memberships = sp.GetRequiredService<IUnitMembershipRepository>();
        var subunits = sp.GetRequiredService<IUnitSubunitMembershipRepository>();

        var ancestorChain = await BuildAncestorChainAsync(
            caller, memberships, subunits, cancellationToken).ConfigureAwait(false);

        if (ancestorChain.Count == 0)
        {
            throw new SpringException(
                $"Caller {caller} has no parent unit; no GitHub binding to resolve.");
        }

        // Walk the chain in order — direct parents first, then ancestors —
        // and return the first GitHub-typed binding. Matches the precedence
        // ToolGrantResolver uses when surfacing the tool to the caller in
        // the first place, so the binding we mint a token against is the
        // same one the grant-resolver picked when deciding the tool was
        // visible.
        foreach (var unitId in ancestorChain)
        {
            var unitIdStr = GuidFormatter.Format(unitId);
            var binding = await configStore.GetAsync(unitIdStr, cancellationToken)
                .ConfigureAwait(false);
            if (binding is null || binding.TypeId != GitHubConnectorType.GitHubTypeId)
            {
                continue;
            }

            UnitGitHubConfig? config;
            try
            {
                config = binding.Config.Deserialize<UnitGitHubConfig>(BindingJson);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex,
                    "github.get_installation_token: binding on unit {Unit:N} carries a " +
                    "malformed config; skipping. Caller={Caller}.",
                    unitId, caller);
                continue;
            }
            if (config is null)
            {
                continue;
            }

            var credential = await _authResolver
                .ResolveAsync(config, cancellationToken)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "github.get_installation_token resolved a {Kind} credential for caller " +
                "{Caller} via binding on unit {Unit:N}.",
                credential.Kind, caller, unitId);

            return SerializeCredential(credential, unitId);
        }

        throw new SpringException(
            $"Caller {caller} has no unit in its parent chain bound to the GitHub " +
            "connector. Bind a parent unit to a GitHub repository before calling " +
            $"{GetInstallationTokenTool}.");
    }

    private static async Task<IReadOnlyList<Guid>> BuildAncestorChainAsync(
        Address caller,
        IUnitMembershipRepository memberships,
        IUnitSubunitMembershipRepository subunits,
        CancellationToken cancellationToken)
    {
        var chain = new List<Guid>();
        var visited = new HashSet<Guid>();
        var frontier = new Queue<Guid>();

        var isUnit = string.Equals(caller.Scheme, Address.UnitScheme, StringComparison.Ordinal);
        if (isUnit)
        {
            frontier.Enqueue(caller.Id);
        }
        else
        {
            var rows = await memberships.ListByAgentAsync(caller.Id, cancellationToken)
                .ConfigureAwait(false);
            foreach (var row in rows)
            {
                frontier.Enqueue(row.UnitId);
            }
        }

        while (frontier.Count > 0)
        {
            var unitId = frontier.Dequeue();
            if (!visited.Add(unitId))
            {
                continue;
            }
            chain.Add(unitId);

            var parents = await subunits.ListByChildAsync(unitId, cancellationToken)
                .ConfigureAwait(false);
            foreach (var p in parents)
            {
                // Tenant-root edges (parent == self) are terminal — skip
                // them to match the walk in ToolGrantResolver.
                if (p.ParentId == unitId)
                {
                    continue;
                }
                frontier.Enqueue(p.ParentId);
            }
        }

        return chain;
    }

    private static Address ParseCaller(ToolCallContext context)
    {
        if (context is null)
        {
            throw new SpringException("Tool call context is missing.");
        }
        if (string.IsNullOrWhiteSpace(context.CallerId))
        {
            throw new SpringException("Tool call context is missing the caller id.");
        }
        if (!GuidFormatter.TryParse(context.CallerId, out var guid))
        {
            throw new SpringException($"Caller id '{context.CallerId}' is not a parseable Guid.");
        }
        var scheme = string.IsNullOrWhiteSpace(context.CallerKind)
            ? Address.AgentScheme
            : context.CallerKind;
        return new Address(scheme, guid);
    }

    private static JsonElement SerializeCredential(GitHubAuthCredential credential, Guid bindingOwnerUnitId)
    {
        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("token", credential.Token);
            writer.WriteString(
                "kind",
                credential.Kind == GitHubAuthCredentialKind.AppInstallation
                    ? "app_installation"
                    : "pat");
            if (credential.ExpiresAt is { } expiresAt)
            {
                writer.WriteString("expires_at", expiresAt.ToString("o", CultureInfo.InvariantCulture));
            }
            else
            {
                writer.WriteNull("expires_at");
            }
            writer.WriteString("binding_owner_unit_id", GuidFormatter.Format(bindingOwnerUnitId));
            writer.WriteEndObject();
        }
        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    private static JsonElement ParseSchema(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static readonly JsonSerializerOptions BindingJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };
}
