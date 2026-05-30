// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Tests.GitHubApp;

using System;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

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
            RedirectUrl: "http://127.0.0.1:54321/",
            OAuthCallbackUrl: "https://example.com/api/v1/tenant/connectors/github/oauth/callback");

        var json = GitHubAppManifest.BuildJson(inputs);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("name").GetString().ShouldBe("Spring Voyage (test)");
        root.GetProperty("hook_attributes").GetProperty("url").GetString()
            .ShouldBe("https://example.com/api/v1/webhooks/github");
        root.GetProperty("hook_attributes").GetProperty("active").GetBoolean().ShouldBeTrue();

        // redirect_url is the loopback handshake target; callback_urls is the
        // deployment's runtime OAuth callback — they are NOT the same URL.
        root.GetProperty("redirect_url").GetString().ShouldBe("http://127.0.0.1:54321/");
        root.GetProperty("public").GetBoolean().ShouldBeFalse();

        var callbacks = root.GetProperty("callback_urls");
        callbacks.GetArrayLength().ShouldBe(1);
        callbacks[0].GetString().ShouldBe("https://example.com/api/v1/tenant/connectors/github/oauth/callback");

        var events = root.GetProperty("default_events");
        events.GetArrayLength().ShouldBe(4);

        var perms = root.GetProperty("default_permissions");
        perms.GetProperty("issues").GetString().ShouldBe("read");
        perms.GetProperty("issue_comment").GetString().ShouldBe("write");
    }

    [Fact]
    public void BuildJson_CallbackUrls_FallBackToRedirect_WhenOAuthCallbackOmitted()
    {
        // Callers that don't drive runtime OAuth (older single-URL shape)
        // collapse callback_urls onto the redirect URL.
        var inputs = new GitHubAppManifest.Inputs(
            Name: "x",
            WebhookUrl: "https://example.com/api/v1/webhooks/github",
            RedirectUrl: "http://127.0.0.1:9/");

        var json = GitHubAppManifest.BuildJson(inputs);
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("callback_urls")[0].GetString().ShouldBe("http://127.0.0.1:9/");
    }

    [Fact]
    public void BuildPostActionUrl_UserScope_UsesSettingsPathWithState()
    {
        GitHubAppManifest.BuildPostActionUrl(org: null, state: "abc123")
            .ShouldBe("https://github.com/settings/apps/new?state=abc123");
    }

    [Fact]
    public void BuildPostActionUrl_OrgScope_UsesOrgPath()
    {
        GitHubAppManifest.BuildPostActionUrl(org: "my-org", state: "s")
            .ShouldBe("https://github.com/organizations/my-org/settings/apps/new?state=s");
    }

    [Fact]
    public void BuildPostActionUrl_EscapesOrgSlugAndState()
    {
        // The org slug travels through the URL path and the nonce through the
        // query string; both go through Uri.EscapeDataString.
        var url = GitHubAppManifest.BuildPostActionUrl(org: "org with space", state: "a b");
        url.ShouldContain("/organizations/org%20with%20space/settings/apps/new");
        url.ShouldContain("state=a%20b");
    }

    [Fact]
    public void BuildAutoSubmitFormHtml_PostsRawManifestJsonToGitHub()
    {
        var inputs = new GitHubAppManifest.Inputs(
            Name: "Spring Voyage",
            WebhookUrl: "https://example.com/api/v1/webhooks/github",
            RedirectUrl: "http://127.0.0.1:8080/",
            OAuthCallbackUrl: "https://example.com/api/v1/tenant/connectors/github/oauth/callback");

        var html = GitHubAppManifest.BuildAutoSubmitFormHtml(inputs, org: null, state: "nonce-1");

        // A POST form pointed at GitHub's create-App endpoint, carrying the
        // nonce, that auto-submits (with a no-JS fallback button).
        html.ShouldContain("method=\"post\"");
        html.ShouldContain("action=\"https://github.com/settings/apps/new?state=nonce-1\"");
        html.ShouldContain("name=\"manifest\"");
        html.ShouldContain("submit()");
        html.ShouldContain("<button");

        // The hidden field carries the HTML-encoded manifest JSON. Decoding it
        // must round-trip to exactly what BuildJson produces — that's the raw
        // JSON the browser hands GitHub when it submits the form. This is the
        // crux of the fix: GitHub only reads the manifest as a POST field, not
        // as a base64 GET query param.
        var value = ExtractManifestInputValue(html);
        WebUtility.HtmlDecode(value).ShouldBe(GitHubAppManifest.BuildJson(inputs));
    }

    [Fact]
    public void BuildAutoSubmitFormHtml_OrgScope_PostsToOrgEndpoint()
    {
        var inputs = new GitHubAppManifest.Inputs(
            "x", "https://example.com/api/v1/webhooks/github", "http://127.0.0.1:1/");
        var html = GitHubAppManifest.BuildAutoSubmitFormHtml(inputs, org: "acme", state: "s");
        html.ShouldContain("action=\"https://github.com/organizations/acme/settings/apps/new?state=s\"");
    }

    [Fact]
    public void BuildJson_RejectsEmptyName()
    {
        Should.Throw<ArgumentException>(() =>
            GitHubAppManifest.BuildJson(new GitHubAppManifest.Inputs(
                Name: "",
                WebhookUrl: "https://example.com/api/v1/webhooks/github",
                RedirectUrl: "http://127.0.0.1:1/")));
    }

    [Fact]
    public void BuildJson_RejectsEmptyWebhookUrl()
    {
        Should.Throw<ArgumentException>(() =>
            GitHubAppManifest.BuildJson(new GitHubAppManifest.Inputs(
                Name: "x",
                WebhookUrl: "",
                RedirectUrl: "http://127.0.0.1:1/")));
    }

    [Fact]
    public void BuildJson_RejectsEmptyRedirectUrl()
    {
        Should.Throw<ArgumentException>(() =>
            GitHubAppManifest.BuildJson(new GitHubAppManifest.Inputs(
                Name: "x",
                WebhookUrl: "https://example.com/api/v1/webhooks/github",
                RedirectUrl: "")));
    }

    private static string ExtractManifestInputValue(string html)
    {
        // The encoded manifest uses &quot; for its embedded quotes, so the
        // attribute value never contains a literal double-quote — a simple
        // value="([^"]*)" capture is safe here.
        var match = Regex.Match(html, "name=\"manifest\"\\s+value=\"([^\"]*)\"");
        match.Success.ShouldBeTrue("expected a hidden manifest input in the form HTML");
        return match.Groups[1].Value;
    }
}
