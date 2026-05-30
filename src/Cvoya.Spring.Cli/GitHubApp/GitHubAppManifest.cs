// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.GitHubApp;

using System;
using System.Collections.Generic;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Builds the JSON payload — and the auto-submitting HTML page — that back
/// GitHub's
/// <see href="https://docs.github.com/en/apps/sharing-github-apps/registering-a-github-app-from-a-manifest">
/// App-from-manifest flow</see>. GitHub accepts the manifest <b>only</b> as a
/// <c>manifest</c> form field on an HTTP <c>POST</c> to
/// <c>/settings/apps/new</c>, carrying the <b>raw JSON</b> string. There is no
/// supported <c>GET ?manifest=…</c> query-string variant — a browser opened at
/// such a URL just renders the blank "create App" form, and the manifest is
/// silently dropped. So the CLI serves a tiny local page whose form auto-POSTs
/// the manifest (see <see cref="BuildAutoSubmitFormHtml"/>); the browser is
/// pointed at that local page, not at github.com directly.
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
    /// Permissions requested on App creation, keyed by GitHub's permission
    /// resource names. These MUST be valid permission resources — GitHub
    /// rejects the entire manifest if any key isn't real (notably
    /// <c>issue_comment</c> is a webhook EVENT, not a permission; posting
    /// issue and PR-conversation comments is granted by <c>issues: write</c>).
    /// Keep the set MINIMAL — GitHub warns users on each extra permission and
    /// every extra scope adds blast radius on a compromised key.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Permissions { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // issues: write — read issue/PR metadata AND post comments on
            // behalf of agents. PR *conversation* comments are created through
            // the issues API, so issues:write covers commenting on issues and
            // PRs alike (PR review comments on the diff would additionally need
            // pull_requests:write — not requested until a skill needs it).
            ["issues"] = "write",
            ["pull_requests"] = "read",
            ["contents"] = "read",
            // metadata: read is mandatory for every App.
            ["metadata"] = "read",
            // statuses / checks: write — set commit statuses and open check
            // runs on agent-driven runs.
            ["statuses"] = "write",
            ["checks"] = "write",
        };

    /// <summary>
    /// Webhook events the connector subscribes to. Each must be a subscribable
    /// event backed by one of <see cref="Permissions"/>, or GitHub rejects the
    /// manifest. <c>installation</c> is intentionally absent: GitHub delivers
    /// installation / installation_repositories events to every App
    /// automatically, and listing it in <c>default_events</c> is rejected
    /// ("Default events unsupported: installation") — the platform still
    /// receives those events without subscribing.
    /// </summary>
    public static IReadOnlyList<string> WebhookEvents { get; } =
        new[] { "issues", "pull_request", "issue_comment" };

    /// <summary>
    /// Inputs to manifest creation. <see cref="Name"/> must be globally
    /// unique on github.com — GitHub rejects name collisions at the
    /// conversion step with a specific error message.
    /// </summary>
    /// <param name="Name">Globally-unique App name on github.com.</param>
    /// <param name="WebhookUrl">The connector's webhook ingress URL (<c>hook_attributes.url</c>).</param>
    /// <param name="RedirectUrl">
    /// Where GitHub sends the browser (with the one-time <c>?code=</c>) after
    /// the operator clicks <b>Create</b>. This is the CLI's <b>loopback</b>
    /// listener — it exists only for the duration of the registration
    /// handshake. Distinct from <see cref="OAuthCallbackUrl"/>.
    /// </param>
    /// <param name="OAuthCallbackUrl">
    /// The deployment's user-to-server OAuth callback (<c>callback_urls</c>) —
    /// where GitHub returns end users after they authorize the App at runtime.
    /// Must match the connector's <c>GitHub__OAuth__RedirectUri</c>. When
    /// <c>null</c> it falls back to <see cref="RedirectUrl"/> to preserve the
    /// single-URL behaviour for callers that don't drive runtime OAuth.
    /// </param>
    public sealed record Inputs(
        string Name,
        string WebhookUrl,
        string RedirectUrl,
        string? OAuthCallbackUrl = null,
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
        if (string.IsNullOrWhiteSpace(inputs.RedirectUrl))
        {
            throw new ArgumentException("Redirect URL is required.", nameof(inputs));
        }

        // redirect_url is the loopback (one-time conversion handshake);
        // callback_urls is the deployment's runtime OAuth callback. They are
        // genuinely different endpoints — only the legacy single-URL caller
        // collapses them.
        var oauthCallback = string.IsNullOrWhiteSpace(inputs.OAuthCallbackUrl)
            ? inputs.RedirectUrl
            : inputs.OAuthCallbackUrl;

        // GitHub ties webhook events to a reachable hook: subscribing to events
        // REQUIRES a non-blank, publicly-reachable hook URL. A loopback webhook
        // (localhost on a dev install) satisfies neither — GitHub rejects the
        // localhost URL ("Hook url is not supported … isn't reachable", even
        // with active:false) AND rejects an empty hook when events are present
        // ("Hook url cannot be blank"). So for a loopback webhook we OMIT BOTH
        // the hook and the event subscriptions: the result is a valid API-only
        // App, and local dev delivers events via `gh webhook forward` (a
        // repo-level channel, independent of the App's hook). A public
        // deployment gets the active hook + event subscriptions as usual.
        var publicHook = IsPubliclyReachableHook(inputs.WebhookUrl);
        var hookAttributes = publicHook ? new HookAttributes(Url: inputs.WebhookUrl, Active: true) : null;
        var defaultEvents = publicHook ? WebhookEvents : null;

        var manifest = new ManifestPayload(
            Name: inputs.Name,
            Url: inputs.HomepageUrl ?? "https://github.com/cvoya-com/spring-voyage",
            HookAttributes: hookAttributes,
            RedirectUrl: inputs.RedirectUrl,
            CallbackUrls: new[] { oauthCallback },
            Description: inputs.Description
                ?? "Spring Voyage GitHub connector — registered via `spring github-app register`.",
            Public: false,
            DefaultEvents: defaultEvents,
            DefaultPermissions: Permissions);

        // GitHub's manifest schema is snake_case, declared explicitly on each
        // property below.
        return JsonSerializer.Serialize(manifest, s_serializerOptions);
    }

    /// <summary>
    /// Whether a webhook URL is reachable over the public Internet. False for
    /// loopback hosts (<c>localhost</c>, <c>127.0.0.0/8</c>, <c>::1</c>,
    /// <c>*.localhost</c>) — GitHub's manifest validator refuses a hook that
    /// isn't publicly reachable (even an inactive one), so a loopback hook is
    /// omitted from the manifest rather than failing the whole registration.
    /// </summary>
    internal static bool IsPubliclyReachableHook(string webhookUrl)
    {
        if (!Uri.TryCreate(webhookUrl, UriKind.Absolute, out var uri))
        {
            // Unparseable — don't second-guess the operator; leave it active.
            return true;
        }
        var host = uri.Host;
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return !(IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip));
    }

    /// <summary>
    /// Builds the GitHub endpoint the manifest form POSTs to. When
    /// <paramref name="org"/> is supplied the App is registered under the
    /// org's settings instead of the authenticated user's account. The
    /// <paramref name="state"/> nonce is echoed back on GitHub's redirect so
    /// the CLI can reject stray / forged callbacks (CSRF protection per
    /// GitHub's docs).
    /// </summary>
    public static string BuildPostActionUrl(string? org, string state)
    {
        var prefix = string.IsNullOrWhiteSpace(org)
            ? "https://github.com/settings/apps/new"
            : $"https://github.com/organizations/{Uri.EscapeDataString(org)}/settings/apps/new";
        return string.IsNullOrEmpty(state)
            ? prefix
            : $"{prefix}?state={Uri.EscapeDataString(state)}";
    }

    /// <summary>
    /// Builds the self-contained HTML page the CLI's loopback listener serves
    /// to the browser. The page carries the raw-JSON manifest in a hidden
    /// <c>manifest</c> field and auto-submits a <c>POST</c> to GitHub — the
    /// only delivery shape GitHub's manifest flow accepts. A visible
    /// "Continue to GitHub" button is the no-JS fallback. After GitHub renders
    /// the pre-filled create-App page and the operator clicks <b>Create</b>,
    /// GitHub redirects back to <see cref="Inputs.RedirectUrl"/> (the same
    /// loopback) with <c>?code=…&amp;state=…</c>.
    /// </summary>
    public static string BuildAutoSubmitFormHtml(Inputs inputs, string? org, string state)
    {
        // Raw JSON goes into a double-quoted attribute value; HtmlEncode
        // escapes the embedded quotes (and &, <, >). The browser decodes it
        // back to the exact JSON string when it submits the form, so GitHub
        // receives the manifest verbatim.
        var manifestAttr = WebUtility.HtmlEncode(BuildJson(inputs));
        var actionAttr = WebUtility.HtmlEncode(BuildPostActionUrl(org, state));

        return "<!doctype html>\n"
            + "<html lang=\"en\">\n"
            + "<head>\n"
            + "  <meta charset=\"utf-8\">\n"
            + "  <title>Spring Voyage — opening GitHub…</title>\n"
            + "  <style>\n"
            + "    body { font-family: system-ui, -apple-system, sans-serif; max-width: 40rem; margin: 4rem auto; padding: 0 1rem; color: #1f2328; }\n"
            + "    h1 { font-size: 1.25rem; }\n"
            + "    button { font: inherit; padding: 0.5rem 1rem; border-radius: 6px; border: 1px solid #1f883d; background: #1f883d; color: #fff; cursor: pointer; }\n"
            + "  </style>\n"
            + "</head>\n"
            + "<body>\n"
            + "  <h1>Sending your GitHub App manifest to GitHub…</h1>\n"
            + "  <p>You should be redirected to GitHub's pre-filled \"Create GitHub App\" page. "
            + "If nothing happens, click the button below.</p>\n"
            + "  <form id=\"sv-manifest-form\" method=\"post\" action=\"" + actionAttr + "\">\n"
            + "    <input type=\"hidden\" name=\"manifest\" value=\"" + manifestAttr + "\">\n"
            + "    <button type=\"submit\">Continue to GitHub &rarr;</button>\n"
            + "  </form>\n"
            + "  <script>document.getElementById('sv-manifest-form').submit();</script>\n"
            + "</body>\n"
            + "</html>\n";
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
        [property: JsonPropertyName("hook_attributes")] HookAttributes? HookAttributes,
        [property: JsonPropertyName("redirect_url")] string RedirectUrl,
        [property: JsonPropertyName("callback_urls")] IReadOnlyList<string> CallbackUrls,
        [property: JsonPropertyName("description")] string Description,
        [property: JsonPropertyName("public")] bool Public,
        [property: JsonPropertyName("default_events")] IReadOnlyList<string>? DefaultEvents,
        [property: JsonPropertyName("default_permissions")] IReadOnlyDictionary<string, string> DefaultPermissions);

    internal sealed record HookAttributes(
        [property: JsonPropertyName("url")] string Url,
        [property: JsonPropertyName("active")] bool Active);
}
