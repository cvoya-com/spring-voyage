// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.GitHubApp;

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Builds the JSON payload that backs GitHub's
/// <see href="https://docs.github.com/en/apps/sharing-github-apps/registering-a-github-app-from-a-manifest">
/// App-from-manifest flow</see>. The manifest is POSTed to GitHub as a
/// <c>manifest</c> form field on the creation URL — no authentication is
/// involved until after the user confirms and GitHub redirects back with a
/// one-time conversion code.
/// </summary>
/// <remarks>
/// The permission and webhook-event sets MUST match what the shipped
/// connector skill bundles actually use. Changing them here without also
/// updating the connector (or vice-versa) silently breaks App installs in
/// production, because GitHub only surfaces granted permissions when an
/// App is created — an unprivileged App issues 404s on unreachable APIs
/// rather than a diagnosable "missing permission" error.
/// </remarks>
public static class GitHubAppManifest
{
    /// <summary>
    /// Hardcoded permissions requested on App creation. The OSS connector
    /// relies on exactly these scopes; the private cloud repo extends via
    /// runtime OAuth grant rather than requesting additional App scopes.
    /// Keep this set MINIMAL — GitHub warns users on each extra permission
    /// and every extra scope adds blast radius on a compromised key.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Permissions { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Read scopes — the connector consumes issues, PRs, and file
            // contents. `metadata: read` is mandatory for every App.
            ["issues"] = "read",
            ["pull_requests"] = "read",
            ["contents"] = "read",
            ["metadata"] = "read",
            // Write scopes — the connector posts comments, opens check
            // runs, and sets commit statuses on behalf of agents.
            ["issue_comment"] = "write",
            ["statuses"] = "write",
            ["checks"] = "write",
        };

    /// <summary>
    /// Webhook events the connector subscribes to. <c>installation</c> is
    /// required so the platform learns when an operator installs or
    /// uninstalls the App on a new org/repo.
    /// </summary>
    public static IReadOnlyList<string> WebhookEvents { get; } =
        new[] { "issues", "pull_request", "issue_comment", "installation" };

    /// <summary>
    /// Inputs to manifest creation. <see cref="Name"/> must be globally
    /// unique on github.com — GitHub rejects name collisions at the
    /// conversion step with a specific error message.
    /// </summary>
    public sealed record Inputs(
        string Name,
        string WebhookUrl,
        string CallbackUrl,
        string? Description = null,
        string? HomepageUrl = null);

    /// <summary>
    /// Serializes the manifest into the exact JSON shape GitHub expects.
    /// The shape is stable: GitHub has not evolved the manifest fields
    /// since the flow shipped, so we can hand-roll the DTO rather than
    /// pulling in a third-party GitHub SDK just for this call.
    /// </summary>
    public static string BuildJson(Inputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (string.IsNullOrWhiteSpace(inputs.Name))
        {
            throw new ArgumentException("App name is required.", nameof(inputs));
        }
        if (string.IsNullOrWhiteSpace(inputs.WebhookUrl))
        {
            throw new ArgumentException("Webhook URL is required.", nameof(inputs));
        }
        if (string.IsNullOrWhiteSpace(inputs.CallbackUrl))
        {
            throw new ArgumentException("Callback URL is required.", nameof(inputs));
        }

        var manifest = new ManifestPayload(
            Name: inputs.Name,
            Url: inputs.HomepageUrl ?? "https://github.com/cvoya-com/spring-voyage",
            HookAttributes: new HookAttributes(Url: inputs.WebhookUrl, Active: true),
            RedirectUrl: inputs.CallbackUrl,
            CallbackUrls: new[] { inputs.CallbackUrl },
            Description: inputs.Description
                ?? "Spring Voyage GitHub connector — registered via `spring github-app register`.",
            Public: false,
            DefaultEvents: WebhookEvents,
            DefaultPermissions: Permissions);

        // Use camelCase-preserving serialisation: GitHub's manifest schema
        // is snake_case, which we declare explicitly on each property.
        return JsonSerializer.Serialize(manifest, s_serializerOptions);
    }

    /// <summary>
    /// Base64-encodes the manifest for inclusion as a query-string
    /// parameter on GitHub's creation URL. GitHub expects a standard
    /// UTF-8 JSON payload; the encoding protects against
    /// shell/URL escape ambiguity in the manifest's embedded quotes.
    /// </summary>
    public static string BuildEncodedManifest(Inputs inputs)
    {
        var json = BuildJson(inputs);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    /// <summary>
    /// Builds the absolute URL the CLI opens in the user's browser. When
    /// <paramref name="org"/> is supplied the App is registered under the
    /// org's settings instead of the authenticated user's account.
    /// </summary>
    public static string BuildCreationUrl(Inputs inputs, string? org = null)
    {
        var encoded = BuildEncodedManifest(inputs);
        // GitHub's own docs recommend POSTing a form containing the
        // manifest. A query-string variant (`?manifest=<base64>`) is
        // supported for one-shot links — that's what we use because the
        // CLI is redirecting the user, not submitting a form.
        var prefix = string.IsNullOrWhiteSpace(org)
            ? "https://github.com/settings/apps/new"
            : $"https://github.com/organizations/{Uri.EscapeDataString(org)}/settings/apps/new";
        return $"{prefix}?manifest={Uri.EscapeDataString(encoded)}";
    }

    // ----- DTO -----------------------------------------------------------
    //
    // System.Text.Json serializes these records directly. Kept `internal`
    // so they are not a public API contract — we want freedom to evolve
    // the shape if GitHub changes the schema.

    private static readonly JsonSerializerOptions s_serializerOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    internal sealed record ManifestPayload(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("url")] string Url,
        [property: JsonPropertyName("hook_attributes")] HookAttributes HookAttributes,
        [property: JsonPropertyName("redirect_url")] string RedirectUrl,
        [property: JsonPropertyName("callback_urls")] IReadOnlyList<string> CallbackUrls,
        [property: JsonPropertyName("description")] string Description,
        [property: JsonPropertyName("public")] bool Public,
        [property: JsonPropertyName("default_events")] IReadOnlyList<string> DefaultEvents,
        [property: JsonPropertyName("default_permissions")] IReadOnlyDictionary<string, string> DefaultPermissions);

    internal sealed record HookAttributes(
        [property: JsonPropertyName("url")] string Url,
        [property: JsonPropertyName("active")] bool Active);
}