// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Configuration;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Connector.GitHub.Auth;
using Cvoya.Spring.Core.Configuration;

using Microsoft.Extensions.Options;

/// <summary>
/// Tier-1 requirement covering the GitHub App credentials (<c>GITHUB_APP_ID</c>,
/// <c>GITHUB_APP_PRIVATE_KEY</c>, <c>GITHUB_WEBHOOK_SECRET</c>). Generalises the
/// pre-#616 <c>IGitHubConnectorAvailability</c> seam into the framework: the
/// connector's endpoints consult
/// <see cref="IConfigurationRequirement"/> via the cached report instead of a
/// GitHub-specific interface.
/// </summary>
/// <remarks>
/// <para>
/// <b>Mandatory flag is <c>false</c>.</b> The OSS build ships the GitHub
/// connector but OSS operators bring their own GitHub App (see the Option B+
/// discussion on issue #616). Missing credentials report
/// <see cref="ConfigurationStatus.Disabled"/> with a suggestion that points at
/// the CLI helper (<c>spring github-app register</c>, issue #631).
/// </para>
/// <para>
/// <b>PEM classification.</b> Shares <see cref="GitHubAppCredentialsValidator"/>
/// with the existing <c>PostConfigure</c> hook, so the classification logic
/// (missing / valid / looks-like-path / malformed) lives in one place. A
/// malformed PEM promotes to <see cref="ConfigurationStatus.Invalid"/>
/// regardless of this requirement's <see cref="IsMandatory"/> value — an
/// operator who set a broken key wants to know about it at boot, not at the
/// first webhook.
/// </para>
/// </remarks>
public sealed class GitHubAppConfigurationRequirement(
    IOptions<GitHubConnectorOptions> optionsAccessor) : IConfigurationRequirement
{
    /// <inheritdoc />
    public string RequirementId => "github-app-credentials";

    /// <inheritdoc />
    public string DisplayName => "GitHub App credentials";

    /// <inheritdoc />
    public string SubsystemName => "GitHub Connector";

    /// <inheritdoc />
    public bool IsMandatory => false;

    /// <inheritdoc />
    public IReadOnlyList<string> EnvironmentVariableNames { get; } =
        new[] { "GITHUB_APP_ID", "GITHUB_APP_PRIVATE_KEY", "GITHUB_WEBHOOK_SECRET" };

    /// <inheritdoc />
    public string? ConfigurationSectionPath => "GitHub";

    /// <inheritdoc />
    public string Description =>
        "GitHub App credentials used to mint installation tokens, register webhooks, and validate incoming webhook signatures. Optional — the connector registers in a disabled state when missing.";

    /// <inheritdoc />
    public Uri? DocumentationUrl { get; } =
        new Uri("https://github.com/cvoya-com/spring-voyage/blob/main/docs/guide/deployment.md#optional--connector-credentials", UriKind.Absolute);

    /// <inheritdoc />
    public Task<ConfigurationRequirementStatus> ValidateAsync(CancellationToken cancellationToken) =>
        Task.FromResult(GetCurrentStatus());

    /// <summary>
    /// Synchronously computes the current status from the bound connector
    /// options. Pure function of the options (no I/O), so endpoint code that
    /// needs to short-circuit on "GitHub not configured" can call this
    /// without a thread hop or a cached-report lookup.
    /// </summary>
    public ConfigurationRequirementStatus GetCurrentStatus()
    {
        var options = optionsAccessor.Value;
        var result = GitHubAppCredentialsValidator.Classify(options);

        switch (result.Classification)
        {
            case GitHubAppCredentialsValidator.Kind.Valid:
                {
                    // Options have already been normalised by the PostConfigure hook —
                    // the resolved PEM is in place and the webhook secret is the last
                    // piece we can advise on.
                    if (string.IsNullOrWhiteSpace(options.WebhookSecret))
                    {
                        return ConfigurationRequirementStatus.MetWithWarning(
                            reason: "GitHub App credentials are configured but GITHUB_WEBHOOK_SECRET is empty.",
                            suggestion:
                                "Set GITHUB_WEBHOOK_SECRET to the secret you configured on the App's webhook settings. " +
                                "Incoming webhooks will be rejected until this value matches GitHub's.");
                    }
                    return ConfigurationRequirementStatus.Met();
                }

            case GitHubAppCredentialsValidator.Kind.Missing:
                return ConfigurationRequirementStatus.Disabled(
                    reason: "GitHub App not configured.",
                    suggestion:
                        "Run `spring github-app register` to create one (see issue #631), or set GITHUB_APP_ID / GITHUB_APP_PRIVATE_KEY / GITHUB_WEBHOOK_SECRET manually. See docs/guide/deployment.md.");

            case GitHubAppCredentialsValidator.Kind.LooksLikePath:
            case GitHubAppCredentialsValidator.Kind.Malformed:
                return ConfigurationRequirementStatus.Invalid(
                    reason: result.ErrorMessage ?? "GitHub App private key could not be parsed.",
                    suggestion:
                        "Fix GITHUB_APP_PRIVATE_KEY per the error above. Inline the PEM contents or mount it as a file whose contents parse as PEM.",
                    fatalError: new InvalidOperationException(result.ErrorMessage ?? "GitHub App credentials are malformed."));

            default:
                // Defensive — a new enum value would fall through; surface as Invalid so
                // the operator knows something wrong happened rather than silently skipping.
                return ConfigurationRequirementStatus.Invalid(
                    reason: $"Unexpected GitHub App credentials classification '{result.Classification}'.",
                    suggestion: "File an issue on the Spring Voyage repo — the classifier returned an unrecognised value.");
        }
    }
}