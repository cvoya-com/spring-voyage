// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.GitHubApp;

using System.Collections.Generic;
using System.Text.Json.Serialization;

/// <summary>
/// The subset of fields GitHub's
/// <c>POST /app-manifests/{code}/conversions</c> response returns that the
/// CLI persists locally. GitHub returns ~20 fields; most are portal-only
/// metadata (creation URL, owner avatar, etc.) that aren't needed to
/// operate the connector.
/// </summary>
/// <remarks>
/// All properties are nullable so we can tolerate GitHub dropping a
/// field. The writer warns on any missing core field rather than
/// crashing.
/// </remarks>
public sealed class ManifestConversionResult
{
    /// <summary>Numeric App ID — primary identifier on the API.</summary>
    [JsonPropertyName("id")]
    public long? AppId { get; set; }

    /// <summary>Human-readable slug used in <c>github.com/apps/{slug}</c>.</summary>
    [JsonPropertyName("slug")]
    public string? Slug { get; set; }

    /// <summary>Display name as shown on GitHub.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>
    /// The full PEM block. GitHub returns the contents inline including
    /// the BEGIN/END lines — we write it verbatim.
    /// </summary>
    [JsonPropertyName("pem")]
    public string? Pem { get; set; }

    /// <summary>Shared webhook HMAC secret.</summary>
    [JsonPropertyName("webhook_secret")]
    public string? WebhookSecret { get; set; }

    /// <summary>OAuth client ID (only used for user-auth flows).</summary>
    [JsonPropertyName("client_id")]
    public string? ClientId { get; set; }

    /// <summary>OAuth client secret.</summary>
    [JsonPropertyName("client_secret")]
    public string? ClientSecret { get; set; }

    /// <summary>
    /// The HTML page the operator visits to install the App onto a
    /// specific account/org. Printed in the success message verbatim.
    /// </summary>
    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }

    /// <summary>
    /// The granted permissions as echoed back by GitHub. Populated for
    /// sanity-check logging only — the manifest we sent is the source of
    /// truth; divergence here would indicate GitHub silently rewrote
    /// something.
    /// </summary>
    [JsonPropertyName("permissions")]
    public IReadOnlyDictionary<string, string>? Permissions { get; set; }

    /// <summary>
    /// The events GitHub subscribed the App to, echoed from the manifest
    /// we submitted.
    /// </summary>
    [JsonPropertyName("events")]
    public IReadOnlyList<string>? Events { get; set; }
}