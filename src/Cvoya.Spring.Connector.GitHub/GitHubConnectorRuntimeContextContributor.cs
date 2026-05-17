// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub;

using System.Globalization;
using System.Text.Json;

using Cvoya.Spring.Connector.GitHub.Auth;
using Cvoya.Spring.Connectors;

using Microsoft.Extensions.Logging;

/// <summary>
/// GitHub implementation of the <see cref="IConnectorRuntimeContextContributor"/>
/// seam (#2380). For every container launch whose subject inherits — directly
/// or via the unit hierarchy — a binding to <see cref="GitHubConnectorType"/>,
/// this contributor emits the owner / repo / installation id / reviewer
/// identity plus a freshly-minted, short-lived installation token. The
/// container's <c>gh</c> / <c>git</c> tooling uses the token to authenticate
/// against the bound repository.
/// </summary>
/// <remarks>
/// <para>
/// <b>Per-launch lifecycle.</b> The token is minted inside
/// <see cref="ContributeAsync"/> and lives only for the duration of the
/// container. Rotation happens by re-launching — the seam contract explicitly
/// disallows caching tokens across launches.
/// </para>
/// <para>
/// <b>Env vars produced.</b>
/// <list type="bullet">
///   <item><description><c>SPRING_CONNECTOR_GITHUB_OWNER</c> — repository owner login.</description></item>
///   <item><description><c>SPRING_CONNECTOR_GITHUB_REPO</c> — repository name.</description></item>
///   <item><description><c>SPRING_CONNECTOR_GITHUB_INSTALLATION_ID</c> — GitHub App installation id (decimal).</description></item>
///   <item><description><c>SPRING_CONNECTOR_GITHUB_REVIEWER</c> — chosen reviewer login (omitted when the binding declares no reviewer).</description></item>
///   <item><description><c>SPRING_CONNECTOR_GITHUB_TOKEN</c> — short-lived installation access token (the value GitHub returns from <c>POST /app/installations/{id}/access_tokens</c>).</description></item>
///   <item><description><c>SPRING_CONNECTOR_GITHUB_TOKEN_EXPIRES_AT</c> — ISO-8601 UTC expiry the container can use to plan its own work without re-minting.</description></item>
/// </list>
/// </para>
/// <para>
/// <b>Context file produced.</b> A single JSON file at
/// <c>connectors/github/binding.json</c> mirrors the env-var fields plus
/// the binding source unit id, so containers can read structured config
/// without re-parsing env-var strings.
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
    GitHubAppAuth auth,
    ILogger<GitHubConnectorRuntimeContextContributor> logger) : IConnectorRuntimeContextContributor
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
    /// Sub-path of the context file the contributor produces, relative to
    /// the canonical <c>/spring/context/</c> mount.
    /// </summary>
    internal const string BindingFilePath = "connectors/github/binding.json";

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
            || string.IsNullOrWhiteSpace(config.Owner)
            || string.IsNullOrWhiteSpace(config.Repo))
        {
            logger.LogWarning(
                "GitHub runtime context: binding on unit {Unit:N} is missing owner / repo; " +
                "skipping the contribution. Subject={Subject}.",
                request.BindingOwnerUnitId, request.Subject);
            return ConnectorRuntimeContextContribution.Empty;
        }

        if (config.AppInstallationId is null or 0)
        {
            // Without an installation id the connector cannot mint a token.
            // The validation surface should have caught this earlier; if it
            // didn't, surface a clean dispatch error so the operator can
            // re-bind with an explicit installation choice.
            logger.LogWarning(
                "GitHub runtime context: binding on unit {Unit:N} has no installation id; " +
                "skipping the contribution. Subject={Subject}.",
                request.BindingOwnerUnitId, request.Subject);
            return ConnectorRuntimeContextContribution.Empty;
        }

        var installationId = config.AppInstallationId.Value;
        InstallationAccessToken minted;
        try
        {
            // Mint directly through the GitHubAppAuth path (the same one
            // GitHubConnector.CreateAuthenticatedClientAsync uses for
            // outbound API calls). The seam contract is per-launch, so
            // we deliberately bypass the installation-token cache here —
            // a fresh token per container launch keeps the credential
            // window short and predictable.
            minted = await auth.MintInstallationTokenAsync(installationId, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Re-throw so the dispatcher surfaces the failure to the
            // operator — silently dropping the contribution would leave
            // the container with no token, which is worse than a clean
            // dispatch failure.
            throw new InvalidOperationException(
                $"Failed to mint a GitHub installation access token for installation {installationId} " +
                $"(unit {request.BindingOwnerUnitId:N}, subject {request.Subject}): {ex.Message}",
                ex);
        }

        var envVars = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [EnvOwner] = config.Owner,
            [EnvRepo] = config.Repo,
            [EnvInstallationId] = installationId.ToString(CultureInfo.InvariantCulture),
            [EnvToken] = minted.Token,
            [EnvTokenExpiresAt] = minted.ExpiresAt.ToString("o", CultureInfo.InvariantCulture),
        };

        // Reviewer is optional — omit the env var entirely when the
        // binding declared no default reviewer so the container sees a
        // clean "not set" signal rather than an empty string. (The
        // PR-without-reviewer flow is tracked in a v0.2 follow-up.)
        if (!string.IsNullOrWhiteSpace(config.Reviewer))
        {
            envVars[EnvReviewer] = config.Reviewer;
        }

        var bindingDoc = new GitHubBindingFile(
            Owner: config.Owner,
            Repo: config.Repo,
            InstallationId: installationId,
            Reviewer: string.IsNullOrWhiteSpace(config.Reviewer) ? null : config.Reviewer,
            OwnerUnitId: request.BindingOwnerUnitId,
            TokenExpiresAt: minted.ExpiresAt);
        var bindingJson = JsonSerializer.Serialize(bindingDoc, BindingFileJson);

        var contextFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [BindingFilePath] = bindingJson,
        };

        logger.LogInformation(
            "GitHub runtime context contributed for subject {Subject}: owner={Owner} repo={Repo} " +
            "installation={InstallationId} reviewer={Reviewer} ownerUnit={OwnerUnit:N}",
            request.Subject, config.Owner, config.Repo, installationId,
            string.IsNullOrWhiteSpace(config.Reviewer) ? "(none)" : config.Reviewer,
            request.BindingOwnerUnitId);

        return new ConnectorRuntimeContextContribution(envVars, contextFiles);
    }

    /// <summary>
    /// JSON shape written to <c>/spring/context/connectors/github/binding.json</c>.
    /// Mirrors the env-var fields so containers that prefer structured input
    /// can read a single file. The token itself is NOT written into the
    /// JSON file — it lives exclusively in the env var so log dumps of the
    /// context mount cannot leak it. <see cref="TokenExpiresAt"/> is
    /// included so the container can plan around the lifetime without
    /// re-decoding the env var.
    /// </summary>
    private sealed record GitHubBindingFile(
        string Owner,
        string Repo,
        long InstallationId,
        string? Reviewer,
        Guid OwnerUnitId,
        DateTimeOffset TokenExpiresAt);
}
