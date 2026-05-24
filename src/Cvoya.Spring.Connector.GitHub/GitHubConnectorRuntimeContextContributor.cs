// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub;

using System.Globalization;
using System.Text.Json;

using Cvoya.Spring.Connector.GitHub.Auth;
using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Messaging;

using Microsoft.Extensions.Logging;

/// <summary>
/// GitHub implementation of both connector-side launch seams: the
/// <see cref="IConnectorRuntimeContextContributor"/> (#2380) and the
/// <see cref="IConnectorPromptContextContributor"/> (#2442). For every
/// container launch whose subject inherits — directly or via the unit
/// hierarchy — a binding to <see cref="GitHubConnectorType"/>, this
/// contributor emits the owner / repo / reviewer identity plus an
/// outbound bearer token resolved through
/// <see cref="GitHubBindingAuthResolver"/> per ADR-0047 §6, AND a
/// platform-layer markdown fragment telling the agent which env-vars it
/// received and how to use them. The container's <c>gh</c> / <c>git</c>
/// tooling uses the token to authenticate against the bound repository;
/// <c>GITHUB_TOKEN</c> is published as a convenience alias so the
/// ecosystem CLIs pick it up natively (#2442).
/// </summary>
/// <remarks>
/// <para>
/// <b>Per-launch lifecycle.</b> The credential is resolved inside
/// <see cref="ContributeAsync"/> and lives only for the duration of the
/// container. On the App branch the value is a freshly-minted short-lived
/// installation token; on the PAT branch the value is the tenant-secret-
/// store entry. Rotation happens by re-launching — the seam contract
/// explicitly disallows caching credentials across launches.
/// </para>
/// <para>
/// <b>Env vars produced.</b>
/// <list type="bullet">
///   <item><description><c>SPRING_CONNECTOR_GITHUB_OWNER</c> — repository owner login.</description></item>
///   <item><description><c>SPRING_CONNECTOR_GITHUB_REPO</c> — repository name.</description></item>
///   <item><description><c>SPRING_CONNECTOR_GITHUB_INSTALLATION_ID</c> — GitHub App installation id (decimal). Only set on the App-installation branch; omitted when the binding uses a PAT.</description></item>
///   <item><description><c>SPRING_CONNECTOR_GITHUB_REVIEWER</c> — chosen reviewer login (omitted when the binding declares no reviewer).</description></item>
///   <item><description><c>SPRING_CONNECTOR_GITHUB_TOKEN</c> — the outbound bearer the container authenticates with (installation access token on the App branch, PAT on the PAT branch).</description></item>
///   <item><description><c>SPRING_CONNECTOR_GITHUB_TOKEN_EXPIRES_AT</c> — ISO-8601 UTC expiry the container can use to plan its own work without re-minting. Only set on the App-installation branch; PAT secrets rotate out-of-band so the contributor does not synthesise an expiry.</description></item>
/// </list>
/// </para>
/// <para>
/// <b>Context file produced.</b> A single JSON file at
/// <c>.spring/connectors/github/binding.json</c> (workspace-relative;
/// the <c>.spring/</c> namespace ADR-0058 reserves for platform-controlled
/// files) mirrors the env-var fields plus the binding source unit id, so
/// containers can read structured config without re-parsing env-var
/// strings.
/// </para>
/// <para>
/// <b>Reviewer field.</b> In OSS the <see cref="UnitGitHubConfig.Reviewer"/>
/// is a static string per binding because every human member maps to the
/// operator. Hosted-platform per-human GitHub-handle resolution lands as
/// a v0.2 follow-up; this contributor emits exactly what the binding
/// declares today.
/// </para>
/// </remarks>
public class GitHubConnectorRuntimeContextContributor(
    GitHubBindingAuthResolver authResolver,
    ILogger<GitHubConnectorRuntimeContextContributor> logger)
    : IConnectorRuntimeContextContributor, IConnectorPromptContextContributor
{
    // Env-var names. Made internal so the unit tests can pin the contract
    // without re-stringing the values.
    internal const string EnvOwner = "SPRING_CONNECTOR_GITHUB_OWNER";
    internal const string EnvRepo = "SPRING_CONNECTOR_GITHUB_REPO";
    internal const string EnvInstallationId = "SPRING_CONNECTOR_GITHUB_INSTALLATION_ID";
    internal const string EnvReviewer = "SPRING_CONNECTOR_GITHUB_REVIEWER";
    internal const string EnvToken = "SPRING_CONNECTOR_GITHUB_TOKEN";
    internal const string EnvTokenExpiresAt = "SPRING_CONNECTOR_GITHUB_TOKEN_EXPIRES_AT";

    /// <summary>
    /// Convenience alias for <see cref="EnvToken"/> (#2442). The <c>gh</c>
    /// CLI and <c>git</c> credential helpers read this name natively, so
    /// publishing it eliminates the "<c>GITHUB_TOKEN=$SPRING_CONNECTOR_GITHUB_TOKEN
    /// gh …</c>" preamble every container would otherwise have to write.
    /// The namespaced var remains canonical; this alias is additive.
    /// </summary>
    internal const string EnvTokenWellKnownAlias = "GITHUB_TOKEN";

    /// <summary>
    /// Sub-path of the context file the contributor produces, relative to
    /// the workspace root. Sits under the <c>.spring/</c> namespace per
    /// ADR-0058 so connector contributions cannot collide with user-managed
    /// project content at the workspace root.
    /// </summary>
    internal const string BindingFilePath = ".spring/connectors/github/binding.json";

    private static readonly JsonSerializerOptions BindingFileJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    /// <inheritdoc />
    public Guid ConnectorTypeId => GitHubConnectorType.GitHubTypeId;

    /// <inheritdoc />
    public async Task<ConnectorRuntimeContextContribution> ContributeAsync(
        ConnectorRuntimeContextRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        UnitGitHubConfig? config;
        try
        {
            config = request.Binding.Config.Deserialize<UnitGitHubConfig>(BindingFileJson);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex,
                "GitHub runtime context: binding on unit {Unit:N} carries a malformed config; " +
                "skipping the contribution. Subject={Subject}.",
                request.BindingOwnerUnitId, request.Subject);
            return ConnectorRuntimeContextContribution.Empty;
        }

        if (config is null
            || !UnitGitHubConfig.TryParseRepo(config.Repo, out var owner, out var repoName))
        {
            logger.LogWarning(
                "GitHub runtime context: binding on unit {Unit:N} is missing a qualified " +
                "'owner/repo' value; skipping the contribution. Subject={Subject}.",
                request.BindingOwnerUnitId, request.Subject);
            return ConnectorRuntimeContextContribution.Empty;
        }

        GitHubAuthCredential credential;
        try
        {
            // ADR-0047 §6: the binding-create gate (§11) pins exactly one
            // of AppInstallationId / PatSecretName; the resolver
            // dispatches on whichever is set. The seam contract is
            // per-launch — the resolver's installation-token-cache layer
            // coalesces concurrent mints across launches without leaking
            // a stale token to a launching container.
            credential = await authResolver.ResolveAsync(config, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (GitHubBindingAuthMissingException ex)
        {
            // Re-throw so the dispatcher surfaces the failure to the
            // operator — silently dropping the contribution would leave
            // the container with no token, which is worse than a clean
            // dispatch failure. Preserve the structured code by passing
            // the typed exception unchanged.
            logger.LogWarning(ex,
                "GitHub runtime context: binding auth missing for unit {Unit:N}; " +
                "failing the launch. Subject={Subject}.",
                request.BindingOwnerUnitId, request.Subject);
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to resolve a GitHub credential for unit {request.BindingOwnerUnitId:N} " +
                $"(subject {request.Subject}): {ex.Message}",
                ex);
        }

        var envVars = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [EnvOwner] = owner,
            [EnvRepo] = repoName,
            [EnvToken] = credential.Token,
        };

        // Installation-id env-var only makes sense on the App branch;
        // PAT bindings have no installation id. The container's gh / git
        // tooling uses the token alone — installation_id is for
        // contributors that mint additional installation-scoped tokens
        // (none in OSS today).
        if (credential.Kind == GitHubAuthCredentialKind.AppInstallation
            && config.AppInstallationId is { } installationId and > 0)
        {
            envVars[EnvInstallationId] = installationId.ToString(CultureInfo.InvariantCulture);
        }

        // Token-expiry env-var is only set on the App branch. PATs rotate
        // out-of-band; the resolver does not synthesise an expiry.
        if (credential.ExpiresAt is { } expiresAt)
        {
            envVars[EnvTokenExpiresAt] = expiresAt.ToString("o", CultureInfo.InvariantCulture);
        }

        // Reviewer is optional — omit the env var entirely when the
        // binding declared no default reviewer so the container sees a
        // clean "not set" signal rather than an empty string. (The
        // PR-without-reviewer flow is tracked in a v0.2 follow-up.)
        if (!string.IsNullOrWhiteSpace(config.Reviewer))
        {
            envVars[EnvReviewer] = config.Reviewer;
        }

        var bindingDoc = new GitHubBindingFile(
            Owner: owner,
            Repo: repoName,
            InstallationId: credential.Kind == GitHubAuthCredentialKind.AppInstallation
                ? config.AppInstallationId
                : null,
            Reviewer: string.IsNullOrWhiteSpace(config.Reviewer) ? null : config.Reviewer,
            OwnerUnitId: request.BindingOwnerUnitId,
            TokenExpiresAt: credential.ExpiresAt);
        var bindingJson = JsonSerializer.Serialize(bindingDoc, BindingFileJson);

        var contextFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [BindingFilePath] = bindingJson,
        };

        // #2442: publish GITHUB_TOKEN alongside SPRING_CONNECTOR_GITHUB_TOKEN.
        // Both names hold the same value; the namespaced one stays canonical,
        // the alias is the convenience hop for gh / git auth.
        var aliasVars = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [EnvTokenWellKnownAlias] = credential.Token,
        };

        logger.LogInformation(
            "GitHub runtime context contributed for subject {Subject}: owner={Owner} repo={Repo} " +
            "kind={Kind} reviewer={Reviewer} ownerUnit={OwnerUnit:N}",
            request.Subject, owner, repoName, credential.Kind,
            string.IsNullOrWhiteSpace(config.Reviewer) ? "(none)" : config.Reviewer,
            request.BindingOwnerUnitId);

        return new ConnectorRuntimeContextContribution(envVars, contextFiles, aliasVars);
    }

    /// <inheritdoc cref="IConnectorPromptContextContributor.GetPromptHintsAsync"/>
    public Task<string?> GetPromptHintsAsync(
        Address subject,
        Guid bindingOwnerUnitId,
        UnitConnectorBinding binding,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(binding);

        UnitGitHubConfig? config;
        try
        {
            config = binding.Config.Deserialize<UnitGitHubConfig>(BindingFileJson);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex,
                "GitHub prompt context: binding on unit {Unit:N} carries a malformed config; " +
                "skipping the hint. Subject={Subject}.",
                bindingOwnerUnitId, subject);
            return Task.FromResult<string?>(null);
        }

        if (config is null
            || !UnitGitHubConfig.TryParseRepo(config.Repo, out var owner, out var repoName))
        {
            logger.LogWarning(
                "GitHub prompt context: binding on unit {Unit:N} is missing a qualified " +
                "'owner/repo' value; skipping the hint. Subject={Subject}.",
                bindingOwnerUnitId, subject);
            return Task.FromResult<string?>(null);
        }

        var fragment = BuildPromptFragment(owner, repoName);
        return Task.FromResult<string?>(fragment);
    }

    /// <summary>
    /// Builds the GitHub-side prompt-context fragment for the bound
    /// repository. Exposed as an internal static so a snapshot test can
    /// pin the exact rendered text against the issue body's contract.
    /// The fragment opens with a <c>### …</c> sub-heading so multiple
    /// connectors render cleanly side-by-side under the single
    /// platform-emitted "Connector context" section heading.
    /// </summary>
    internal static string BuildPromptFragment(string owner, string repo)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);

        return $$"""
            ### GitHub binding — {{owner}}/{{repo}}

            Your container has GitHub credentials and repo identity injected as env-vars:

            - $SPRING_CONNECTOR_GITHUB_OWNER       — repo owner ({{owner}})
            - $SPRING_CONNECTOR_GITHUB_REPO        — repo name ({{repo}})
            - $SPRING_CONNECTOR_GITHUB_REVIEWER    — operator's GitHub login for review requests / assignee fallback
            - $SPRING_CONNECTOR_GITHUB_TOKEN       — outbound bearer token (also exposed as $GITHUB_TOKEN for gh / git compatibility)
            - $SPRING_CONNECTOR_GITHUB_TOKEN_EXPIRES_AT — token expiry (UTC ISO; only set on the App-installation auth path)

            Use `gh` and `git` against the bound repo:

              REPO="$SPRING_CONNECTOR_GITHUB_OWNER/$SPRING_CONNECTOR_GITHUB_REPO"
              gh issue list --repo "$REPO" --milestone v0.1 --state open

            `gh` and `git` will pick up $GITHUB_TOKEN automatically — no `gh auth login` needed.

            If you need the token from a tool-call shape (e.g. a non-CLI HTTP client),
            call the platform tool `github.get_installation_token` — it returns the same
            value as $GITHUB_TOKEN plus the credential kind and expiry. **Do not** try to
            fetch the token by constructing an HTTP URL against the platform: no such
            endpoint exists, and the env-var / tool are the only two ways to get it.

            When you receive a message whose payload `source` is `github`, the connector
            translated an inbound webhook into that message. The envelope shape and the
            canonical intent vocabulary are published by the tool
            `github.describe_inbound_contract` — input-less, idempotent, stable across the
            connector's lifetime. Call it once on the first github-source turn and switch
            on the resulting `intent` rather than on the raw `action`.
            """;
    }

    /// <summary>
    /// JSON shape written to <c>.spring/connectors/github/binding.json</c>
    /// (workspace-relative). Mirrors the env-var fields so containers that
    /// prefer structured input can read a single file. The token itself is
    /// NOT written into the JSON file — it lives exclusively in the env
    /// var so log dumps of the workspace cannot leak it.
    /// <see cref="TokenExpiresAt"/> is included so the container can plan
    /// around the lifetime without re-decoding the env var.
    /// </summary>
    private sealed record GitHubBindingFile(
        string Owner,
        string Repo,
        long? InstallationId,
        string? Reviewer,
        Guid OwnerUnitId,
        DateTimeOffset? TokenExpiresAt);
}
