// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Tests.SlackInstall;

using System;
using System.Text.Json;

using Cvoya.Spring.Connector.Slack.Provisioning;

using Shouldly;

using Xunit;

public class SlackAppManifestTests
{
    [Fact]
    public void BuildJson_ProducesExpectedTopLevelShape()
    {
        var json = SlackAppManifest.BuildJson(new SlackAppManifest.Inputs(
            AppName: "Spring Voyage (test)",
            SvHost: "https://sv.example.com"));

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("display_information").GetProperty("name")
            .GetString().ShouldBe("Spring Voyage (test)");
        root.GetProperty("features").GetProperty("bot_user").GetProperty("display_name")
            .GetString().ShouldBe("Spring Voyage (test)");

        var redirectUrls = root.GetProperty("oauth_config").GetProperty("redirect_urls");
        redirectUrls.GetArrayLength().ShouldBe(1);
        redirectUrls[0].GetString()
            .ShouldBe("https://sv.example.com/api/v1/tenant/connectors/slack/oauth/callback");

        root.GetProperty("settings").GetProperty("event_subscriptions").GetProperty("request_url")
            .GetString().ShouldBe("https://sv.example.com/api/v1/tenant/connectors/slack/events");
        root.GetProperty("settings").GetProperty("interactivity").GetProperty("request_url")
            .GetString().ShouldBe("https://sv.example.com/api/v1/tenant/connectors/slack/interactions");
    }

    [Fact]
    public void BuildJson_AppHome_EnablesMessagesTab_PerAdr0061Section22()
    {
        // Regression for #2881: a missing features.app_home block defaults the
        // Messages Tab off, so the bound user sees "Sending messages to this
        // app has been turned off" and cannot DM the bot — breaking the DM-only
        // premise of ADR-0061 §2.2. The manifest must enable the Messages Tab
        // and keep it writable; the Home Tab stays off (no v0.1 Home view).
        var json = SlackAppManifest.BuildJson(new SlackAppManifest.Inputs(
            AppName: "x",
            SvHost: "https://sv.example.com"));

        using var doc = JsonDocument.Parse(json);
        var appHome = doc.RootElement
            .GetProperty("features")
            .GetProperty("app_home");

        appHome.GetProperty("home_tab_enabled").GetBoolean().ShouldBeFalse();
        appHome.GetProperty("messages_tab_enabled").GetBoolean().ShouldBeTrue();
        appHome.GetProperty("messages_tab_read_only_enabled").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public void BuildJson_BotScopes_MatchAdr0061Section6()
    {
        // ADR-0061 §6 fixes the bot scopes; the manifest must request
        // exactly the same set the connector consumes at OAuth time.
        var json = SlackAppManifest.BuildJson(new SlackAppManifest.Inputs(
            AppName: "x",
            SvHost: "https://sv.example.com"));

        using var doc = JsonDocument.Parse(json);
        var botScopes = doc.RootElement
            .GetProperty("oauth_config")
            .GetProperty("scopes")
            .GetProperty("bot");

        botScopes.GetArrayLength().ShouldBe(10);
        var scopeSet = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
        foreach (var scope in botScopes.EnumerateArray())
        {
            scopeSet.Add(scope.GetString()!);
        }
        scopeSet.ShouldContain("chat:write");
        scopeSet.ShouldContain("chat:write.customize");
        scopeSet.ShouldContain("im:history");
        scopeSet.ShouldContain("im:write");
        scopeSet.ShouldContain("im:read");
        scopeSet.ShouldContain("users:read");
        scopeSet.ShouldContain("users:read.email");
        scopeSet.ShouldContain("commands");
        scopeSet.ShouldContain("channels:read");
        scopeSet.ShouldContain("groups:read");
    }

    [Fact]
    public void BuildJson_SlashCommands_AreSvThreadSvThreadsSvHelp()
    {
        var json = SlackAppManifest.BuildJson(new SlackAppManifest.Inputs(
            AppName: "x",
            SvHost: "https://sv.example.com"));

        using var doc = JsonDocument.Parse(json);
        var commands = doc.RootElement
            .GetProperty("features")
            .GetProperty("slash_commands");

        commands.GetArrayLength().ShouldBe(3);
        var names = new System.Collections.Generic.List<string>();
        foreach (var cmd in commands.EnumerateArray())
        {
            names.Add(cmd.GetProperty("command").GetString()!);
            cmd.GetProperty("url").GetString()
                .ShouldBe("https://sv.example.com/api/v1/tenant/connectors/slack/commands");
        }
        names.ShouldContain("/sv-thread");
        names.ShouldContain("/sv-threads");
        names.ShouldContain("/sv-help");
    }

    [Fact]
    public void BuildJson_OrgDeploy_IsDisabled_PerAdr0061Section23()
    {
        var json = SlackAppManifest.BuildJson(new SlackAppManifest.Inputs(
            AppName: "x",
            SvHost: "https://sv.example.com"));

        using var doc = JsonDocument.Parse(json);
        doc.RootElement
            .GetProperty("settings")
            .GetProperty("org_deploy_enabled")
            .GetBoolean()
            .ShouldBeFalse();
    }

    [Fact]
    public void BuildJson_TrailingSlashOnHost_IsNormalized()
    {
        var json = SlackAppManifest.BuildJson(new SlackAppManifest.Inputs(
            AppName: "x",
            SvHost: "https://sv.example.com/"));

        using var doc = JsonDocument.Parse(json);
        doc.RootElement
            .GetProperty("oauth_config")
            .GetProperty("redirect_urls")[0]
            .GetString()
            .ShouldBe("https://sv.example.com/api/v1/tenant/connectors/slack/oauth/callback");
    }

    [Fact]
    public void BuildJson_RequiresAppName()
    {
        Should.Throw<ArgumentException>(() =>
            SlackAppManifest.BuildJson(new SlackAppManifest.Inputs(
                AppName: "",
                SvHost: "https://sv.example.com")));
    }

    [Fact]
    public void BuildJson_RequiresSvHost()
    {
        Should.Throw<ArgumentException>(() =>
            SlackAppManifest.BuildJson(new SlackAppManifest.Inputs(
                AppName: "x",
                SvHost: "")));
    }

    [Fact]
    public void BuildJson_SocketMode_DefaultsToDisabled()
    {
        var json = SlackAppManifest.BuildJson(new SlackAppManifest.Inputs(
            AppName: "x",
            SvHost: "https://sv.example.com"));

        using var doc = JsonDocument.Parse(json);
        doc.RootElement
            .GetProperty("settings")
            .GetProperty("socket_mode_enabled")
            .GetBoolean()
            .ShouldBeFalse();
    }

    [Fact]
    public void BuildJson_SocketModeEnabled_FlipsSetting()
    {
        var json = SlackAppManifest.BuildJson(new SlackAppManifest.Inputs(
            AppName: "x",
            SvHost: "https://sv.example.com",
            SocketModeEnabled: true));

        using var doc = JsonDocument.Parse(json);
        doc.RootElement
            .GetProperty("settings")
            .GetProperty("socket_mode_enabled")
            .GetBoolean()
            .ShouldBeTrue();
    }
}
