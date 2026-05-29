// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Slack.Install;

using System.Collections.Generic;

/// <summary>
/// Server-side equivalent of the <c>spring connector slack install
/// --write-tenant-secrets</c> CLI verb (#2882). Drives Slack's App
/// Manifest API end-to-end — build manifest → validate → create →
/// persist the four OAuth credentials as tenant secrets → mint a
/// state-bearing consent URL — so the portal wizard can register a Slack
/// app without a terminal round-trip.
///
/// <para>
/// The manifest builder and the credential→secret-name mapping are shared
/// with the CLI through the <c>Cvoya.Spring.Connector.Slack.Provisioning</c>
/// kernel, so both surfaces persist byte-identical secrets and produce
/// identical manifests.
/// </para>
/// </summary>
public interface ISlackManifestInstallService
{
    /// <summary>
    /// Runs the install. On <see cref="SlackManifestInstallRequest.DryRun"/>
    /// the manifest JSON is built and returned without contacting Slack or
    /// persisting anything (the portal's manifest preview). Otherwise the
    /// manifest is validated + created against Slack, the credentials are
    /// persisted as tenant secrets with an all-or-nothing rollback
    /// contract, and a state-bearing Slack consent URL is returned.
    /// </summary>
    /// <exception cref="Cvoya.Spring.Connector.Slack.Provisioning.SlackManifestException">
    /// Slack rejected the manifest validation / creation (e.g. an expired
    /// configuration token surfaces as <c>invalid_auth</c>).
    /// </exception>
    Task<SlackManifestInstallResult> InstallAsync(
        SlackManifestInstallRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reports whether this tenant already has a complete set of Slack
    /// OAuth credentials configured (client id, client secret, signing
    /// secret, redirect uri) across any persistence tier (tenant → platform
    /// → env, per <c>ISlackOAuthOptionsResolver</c>). The portal wizard uses
    /// this to offer a "credentials already configured — connect now"
    /// shortcut that skips app registration and jumps straight to OAuth
    /// consent (#2882) — the common case for an operator who already ran
    /// <c>spring connector slack install</c> but has not completed the
    /// consent step.
    /// </summary>
    Task<bool> IsOAuthConfiguredAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Inputs to <see cref="ISlackManifestInstallService.InstallAsync"/>. The
/// endpoint normalises these from the wire request — <see cref="AppName"/>
/// and <see cref="SvHost"/> are already defaulted / validated, and
/// <see cref="ConfigToken"/> is guaranteed non-empty when
/// <see cref="DryRun"/> is <c>false</c>.
/// </summary>
/// <param name="ConfigToken">The Slack Configuration Token. May be null on a dry-run.</param>
/// <param name="AppName">The display name for the new Slack app.</param>
/// <param name="SvHost">The Spring Voyage base URL baked into the manifest's redirect / event / command URLs.</param>
/// <param name="SocketMode">Whether to generate the manifest with Socket Mode enabled.</param>
/// <param name="DryRun">When true, build + return the manifest without contacting Slack or persisting anything.</param>
/// <param name="ClientState">Opaque state echoed through the OAuth handoff (the portal's <c>targetOrigin</c>).</param>
public sealed record SlackManifestInstallRequest(
    string? ConfigToken,
    string AppName,
    string SvHost,
    bool SocketMode,
    bool DryRun,
    string? ClientState);

/// <summary>
/// Result of <see cref="ISlackManifestInstallService.InstallAsync"/>.
/// On a dry-run only <see cref="ManifestJson"/> is populated; the rest are
/// null / empty.
/// </summary>
/// <param name="ManifestJson">The manifest JSON sent to Slack (always present — the wizard renders it as a preview).</param>
/// <param name="DryRun">Echoes the request flag so the caller can branch without re-reading its own input.</param>
/// <param name="AppId">The new Slack app id, or null on a dry-run.</param>
/// <param name="AuthorizeUrl">The state-bearing Slack consent URL the operator's browser must visit, or null on a dry-run.</param>
/// <param name="State">The OAuth state token (surfaced for debugging; never secret), or null on a dry-run.</param>
/// <param name="WrittenSecretNames">The tenant-secret names persisted, in canonical order. Empty on a dry-run.</param>
public sealed record SlackManifestInstallResult(
    string ManifestJson,
    bool DryRun,
    string? AppId,
    string? AuthorizeUrl,
    string? State,
    IReadOnlyList<string> WrittenSecretNames);
