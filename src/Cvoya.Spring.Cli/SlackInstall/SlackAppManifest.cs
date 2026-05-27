// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.SlackInstall;

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Builds the JSON payload Slack expects on its
/// <see href="https://api.slack.com/reference/manifests">App Manifest API</see>
/// — the equivalent of GitHub's App-from-manifest flow. The manifest
/// captures the app's display, scopes, OAuth redirect URLs, event
/// subscriptions, and slash commands so a single CLI call can replace
/// the ~10 minutes of clicking through api.slack.com's admin UI.
/// </summary>
/// <remarks>
/// <para>
/// The bot scope set, slash-command names, and webhook event surface
/// listed here MUST stay aligned with the shipped Slack connector
/// (<c>Cvoya.Spring.Connector.Slack</c>). ADR-0061 §6 fixes the scope
/// set; <c>SlackCommandDispatcher</c> fixes the slash-command names;
/// <c>SlackEventEndpoints</c> fixes the inbound event URL.
/// </para>
/// <para>
/// Enterprise Grid is disabled here per ADR-0061 §2.3 — workspace
/// installs are the only path the connector supports today.
/// </para>
/// </remarks>
public static class SlackAppManifest
{
    /// <summary>
    /// Bot-token scopes requested at install time. Matches the default in
    /// <c>SlackOAuthOptions.Scopes</c> (ADR-0061 §6).
    /// </summary>
    public static IReadOnlyList<string> BotScopes { get; } = new[]
    {
        "chat:write",
        "chat:write.customize",
        "im:history",
        "im:write",
        "im:read",
        "users:read",
        "users:read.email",
        "commands",
        "channels:read",
        "groups:read",
    };

    /// <summary>
    /// Bot events the connector subscribes to. Drives the
    /// <c>POST /api/v1/tenant/connectors/slack/events</c> handler.
    /// </summary>
    public static IReadOnlyList<string> BotEvents { get; } = new[]
    {
        "message.im",
        "member_joined_channel",
    };

    /// <summary>
    /// Slash-command names the connector handles. Constants live on
    /// <c>SlackCommandDispatcher</c> in the connector project; we
    /// duplicate the values here because the CLI must not depend on the
    /// connector assembly (the CLI ships as a separate dotnet tool).
    /// </summary>
    public static IReadOnlyList<string> SlashCommands { get; } = new[]
    {
        "/sv-thread",
        "/sv-threads",
        "/sv-help",
    };

    /// <summary>
    /// Path the OAuth callback lives on, relative to the SV host. Mirrors
    /// <c>SlackOAuthEndpoints.MapPost("/oauth/callback", …)</c> inside the
    /// tenant connector route group.
    /// </summary>
    public const string OAuthCallbackPath = "/api/v1/tenant/connectors/slack/oauth/callback";

    /// <summary>Path the inbound events handler lives on.</summary>
    public const string EventsPath = "/api/v1/tenant/connectors/slack/events";

    /// <summary>Path the slash-command handler lives on.</summary>
    public const string CommandsPath = "/api/v1/tenant/connectors/slack/commands";

    /// <summary>Path the Block Kit interaction handler lives on.</summary>
    public const string InteractionsPath = "/api/v1/tenant/connectors/slack/interactions";

    /// <summary>
    /// Inputs to manifest creation. <see cref="SvHost"/> is the operator's
    /// Spring Voyage base URL (no trailing slash); the manifest builder
    /// concatenates it with the well-known connector paths above.
    /// <see cref="SocketModeEnabled"/> toggles Slack Socket Mode — when true,
    /// Slack delivers events, slash commands, and interactions over a
    /// WebSocket the bot opens outbound instead of POSTing to the manifest
    /// URLs. Required for local-dev installs that cannot expose a public
    /// HTTPS endpoint.
    /// </summary>
    public sealed record Inputs(
        string AppName,
        string SvHost,
        string? Description = null,
        string? LongDescription = null,
        string? BackgroundColor = null,
        bool SocketModeEnabled = false);

    /// <summary>
    /// Serializes the manifest into the exact JSON shape Slack expects
    /// on <c>apps.manifest.validate</c> / <c>apps.manifest.create</c>.
    /// </summary>
    public static string BuildJson(Inputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (string.IsNullOrWhiteSpace(inputs.AppName))
        {
            throw new ArgumentException("App name is required.", nameof(inputs));
        }
        if (string.IsNullOrWhiteSpace(inputs.SvHost))
        {
            throw new ArgumentException("SV host is required.", nameof(inputs));
        }

        var host = inputs.SvHost.TrimEnd('/');
        var redirectUrl = host + OAuthCallbackPath;
        var eventsUrl = host + EventsPath;
        var commandsUrl = host + CommandsPath;
        var interactionsUrl = host + InteractionsPath;

        var manifest = new ManifestPayload(
            DisplayInformation: new DisplayInformation(
                Name: inputs.AppName,
                Description: inputs.Description
                    ?? "Spring Voyage Slack connector — registered via `spring connector slack install`.",
                LongDescription: inputs.LongDescription,
                BackgroundColor: inputs.BackgroundColor),
            Features: new Features(
                BotUser: new BotUser(
                    DisplayName: inputs.AppName,
                    AlwaysOnline: true),
                SlashCommands: BuildSlashCommands(commandsUrl)),
            OAuthConfig: new OAuthConfig(
                RedirectUrls: new[] { redirectUrl },
                Scopes: new Scopes(Bot: BotScopes)),
            Settings: new Settings(
                EventSubscriptions: new EventSubscriptions(
                    RequestUrl: eventsUrl,
                    BotEvents: BotEvents),
                Interactivity: new Interactivity(
                    IsEnabled: true,
                    RequestUrl: interactionsUrl),
                OrgDeployEnabled: false,
                SocketModeEnabled: inputs.SocketModeEnabled,
                TokenRotationEnabled: false));

        return JsonSerializer.Serialize(manifest, s_serializerOptions);
    }

    private static IReadOnlyList<SlashCommand> BuildSlashCommands(string commandsUrl)
    {
        return new[]
        {
            new SlashCommand(
                Command: "/sv-thread",
                Url: commandsUrl,
                Description: "Start a new Spring Voyage thread with one or more agents, units, or humans.",
                UsageHint: null,
                ShouldEscape: false),
            new SlashCommand(
                Command: "/sv-threads",
                Url: commandsUrl,
                Description: "List your active Spring Voyage threads.",
                UsageHint: null,
                ShouldEscape: false),
            new SlashCommand(
                Command: "/sv-help",
                Url: commandsUrl,
                Description: "Show the Spring Voyage cheat sheet.",
                UsageHint: null,
                ShouldEscape: false),
        };
    }

    private static readonly JsonSerializerOptions s_serializerOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ----- DTO -----------------------------------------------------------
    //
    // Internal records — Slack's manifest schema is snake_case, which we
    // declare explicitly. Kept internal so we keep freedom to evolve the
    // shape when Slack publishes a v2 manifest.

    internal sealed record ManifestPayload(
        [property: JsonPropertyName("display_information")] DisplayInformation DisplayInformation,
        [property: JsonPropertyName("features")] Features Features,
        [property: JsonPropertyName("oauth_config")] OAuthConfig OAuthConfig,
        [property: JsonPropertyName("settings")] Settings Settings);

    internal sealed record DisplayInformation(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("description")] string Description,
        [property: JsonPropertyName("long_description")] string? LongDescription,
        [property: JsonPropertyName("background_color")] string? BackgroundColor);

    internal sealed record Features(
        [property: JsonPropertyName("bot_user")] BotUser BotUser,
        [property: JsonPropertyName("slash_commands")] IReadOnlyList<SlashCommand> SlashCommands);

    internal sealed record BotUser(
        [property: JsonPropertyName("display_name")] string DisplayName,
        [property: JsonPropertyName("always_online")] bool AlwaysOnline);

    internal sealed record SlashCommand(
        [property: JsonPropertyName("command")] string Command,
        [property: JsonPropertyName("url")] string Url,
        [property: JsonPropertyName("description")] string Description,
        [property: JsonPropertyName("usage_hint")] string? UsageHint,
        [property: JsonPropertyName("should_escape")] bool ShouldEscape);

    internal sealed record OAuthConfig(
        [property: JsonPropertyName("redirect_urls")] IReadOnlyList<string> RedirectUrls,
        [property: JsonPropertyName("scopes")] Scopes Scopes);

    internal sealed record Scopes(
        [property: JsonPropertyName("bot")] IReadOnlyList<string> Bot);

    internal sealed record Settings(
        [property: JsonPropertyName("event_subscriptions")] EventSubscriptions EventSubscriptions,
        [property: JsonPropertyName("interactivity")] Interactivity Interactivity,
        [property: JsonPropertyName("org_deploy_enabled")] bool OrgDeployEnabled,
        [property: JsonPropertyName("socket_mode_enabled")] bool SocketModeEnabled,
        [property: JsonPropertyName("token_rotation_enabled")] bool TokenRotationEnabled);

    internal sealed record EventSubscriptions(
        [property: JsonPropertyName("request_url")] string RequestUrl,
        [property: JsonPropertyName("bot_events")] IReadOnlyList<string> BotEvents);

    internal sealed record Interactivity(
        [property: JsonPropertyName("is_enabled")] bool IsEnabled,
        [property: JsonPropertyName("request_url")] string RequestUrl);
}
