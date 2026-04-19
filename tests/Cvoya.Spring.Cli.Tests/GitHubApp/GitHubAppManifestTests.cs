// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Tests.GitHubApp;

using System;
using System.Text;
using System.Text.Json;

using Cvoya.Spring.Cli.GitHubApp;

using Shouldly;

using Xunit;

public class GitHubAppManifestTests
{
    [Fact]
    public void Permissions_MatchConnectorContract()
    {
        // This test locks the set of permissions requested at App creation
        // so the connector's skill bundles never silently outgrow them —
        // each additional scope must be added in both places deliberately.
        GitHubAppManifest.Permissions["issues"].ShouldBe("read");
        GitHubAppManifest.Permissions["pull_requests"].ShouldBe("read");
        GitHubAppManifest.Permissions["contents"].ShouldBe("read");
        GitHubAppManifest.Permissions["metadata"].ShouldBe("read");
        GitHubAppManifest.Permissions["issue_comment"].ShouldBe("write");
        GitHubAppManifest.Permissions["statuses"].ShouldBe("write");
        GitHubAppManifest.Permissions["checks"].ShouldBe("write");
        GitHubAppManifest.Permissions.Count.ShouldBe(7);
    }

    [Fact]
    public void WebhookEvents_MatchConnectorContract()
    {
        GitHubAppManifest.WebhookEvents.ShouldBe(new[] { "issues", "pull_request", "issue_comment", "installation" });
    }

    [Fact]
    public void BuildJson_ContainsExpectedFields()
    {
        var inputs = new GitHubAppManifest.Inputs(
            Name: "Spring Voyage (test)",
            WebhookUrl: "https://example.com/api/v1/webhooks/github",
            CallbackUrl: "http://127.0.0.1:54321/");

        var json = GitHubAppManifest.BuildJson(inputs);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("name").GetString().ShouldBe("Spring Voyage (test)");
        root.GetProperty("hook_attributes").GetProperty("url").GetString()
            .ShouldBe("https://example.com/api/v1/webhooks/github");
        root.GetProperty("hook_attributes").GetProperty("active").GetBoolean().ShouldBeTrue();
        root.GetProperty("redirect_url").GetString().ShouldBe("http://127.0.0.1:54321/");
        root.GetProperty("public").GetBoolean().ShouldBeFalse();

        var callbacks = root.GetProperty("callback_urls");
        callbacks.GetArrayLength().ShouldBe(1);
        callbacks[0].GetString().ShouldBe("http://127.0.0.1:54321/");

        var events = root.GetProperty("default_events");
        events.GetArrayLength().ShouldBe(4);

        var perms = root.GetProperty("default_permissions");
        perms.GetProperty("issues").GetString().ShouldBe("read");
        perms.GetProperty("issue_comment").GetString().ShouldBe("write");
    }

    [Fact]
    public void BuildEncodedManifest_DecodesBackToOriginalJson()
    {
        var inputs = new GitHubAppManifest.Inputs(
            Name: "Spring Voyage",
            WebhookUrl: "https://example.com/api/v1/webhooks/github",
            CallbackUrl: "http://127.0.0.1:8080/");

        var encoded = GitHubAppManifest.BuildEncodedManifest(inputs);

        // Base64 characters only; no URL-unsafe content before URL encoding.
        encoded.ShouldNotBeEmpty();
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        decoded.ShouldBe(GitHubAppManifest.BuildJson(inputs));
    }

    [Fact]
    public void BuildCreationUrl_UserScope_UsesSettingsPath()
    {
        var inputs = new GitHubAppManifest.Inputs("x", "https://example.com/api/v1/webhooks/github", "http://127.0.0.1:1/");
        var url = GitHubAppManifest.BuildCreationUrl(inputs);
        url.ShouldStartWith("https://github.com/settings/apps/new?manifest=");
    }

    [Fact]
    public void BuildCreationUrl_OrgScope_UsesOrgPath()
    {
        var inputs = new GitHubAppManifest.Inputs("x", "https://example.com/api/v1/webhooks/github", "http://127.0.0.1:1/");
        var url = GitHubAppManifest.BuildCreationUrl(inputs, org: "my-org");
        url.ShouldStartWith("https://github.com/organizations/my-org/settings/apps/new?manifest=");
    }

    [Fact]
    public void BuildCreationUrl_EncodesOrgSlugWithUnsafeCharacters()
    {
        // The org slug travels through the URL path, not the query
        // string; Uri.EscapeDataString keeps spaces / slashes from
        // producing ambiguous URLs.
        var inputs = new GitHubAppManifest.Inputs("x", "https://example.com/api/v1/webhooks/github", "http://127.0.0.1:1/");
        var url = GitHubAppManifest.BuildCreationUrl(inputs, org: "org with space");
        url.ShouldContain("/organizations/org%20with%20space/settings/apps/new");
    }

    [Fact]
    public void BuildJson_RejectsEmptyName()
    {
        Should.Throw<ArgumentException>(() =>
            GitHubAppManifest.BuildJson(new GitHubAppManifest.Inputs(
                Name: "",
                WebhookUrl: "https://example.com/api/v1/webhooks/github",
                CallbackUrl: "http://127.0.0.1:1/")));
    }

    [Fact]
    public void BuildJson_RejectsEmptyWebhookUrl()
    {
        Should.Throw<ArgumentException>(() =>
            GitHubAppManifest.BuildJson(new GitHubAppManifest.Inputs(
                Name: "x",
                WebhookUrl: "",
                CallbackUrl: "http://127.0.0.1:1/")));
    }

    [Fact]
    public void BuildJson_RejectsEmptyCallbackUrl()
    {
        Should.Throw<ArgumentException>(() =>
            GitHubAppManifest.BuildJson(new GitHubAppManifest.Inputs(
                Name: "x",
                WebhookUrl: "https://example.com/api/v1/webhooks/github",
                CallbackUrl: "")));
    }
}