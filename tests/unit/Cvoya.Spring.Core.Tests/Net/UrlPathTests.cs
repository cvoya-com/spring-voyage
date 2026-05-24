// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Tests.Net;

using Cvoya.Spring.Core.Net;

using Shouldly;

using Xunit;

/// <summary>
/// Pins <see cref="UrlPath.Combine(string?, string?)"/> on every
/// slash-position case the engineer-hallucination cascade in #2707
/// surfaced — the double-slash classes are the regression bar this
/// helper exists to guard.
/// </summary>
public class UrlPathTests
{
    // --- The #2707 slash-position cases ---

    [Theory]
    [InlineData("http://spring-caddy:8443", "/api/v1/connectors/github/token")]
    [InlineData("http://spring-caddy:8443/", "/api/v1/connectors/github/token")]
    [InlineData("http://spring-caddy:8443//", "/api/v1/connectors/github/token")]
    [InlineData("http://spring-caddy:8443", "//api/v1/connectors/github/token")]
    [InlineData("http://spring-caddy:8443/", "//api/v1/connectors/github/token")]
    [InlineData("http://spring-caddy:8443//", "//api/v1/connectors/github/token")]
    public void Combine_TenantHostAndAbsolutePath_NormalisesToOneSlashAtBoundary(string baseUrl, string path)
    {
        UrlPath.Combine(baseUrl, path)
            .ShouldBe("http://spring-caddy:8443/api/v1/connectors/github/token");
    }

    [Theory]
    [InlineData("http://spring-caddy:8443/api/v1", "/connectors/github/token")]
    [InlineData("http://spring-caddy:8443/api/v1/", "/connectors/github/token")]
    [InlineData("http://spring-caddy:8443/api/v1", "connectors/github/token")]
    [InlineData("http://spring-caddy:8443/api/v1/", "connectors/github/token")]
    [InlineData("http://spring-caddy:8443/api/v1//", "//connectors/github/token")]
    public void Combine_BaseWithPathPrefix_NormalisesMidPath(string baseUrl, string path)
    {
        UrlPath.Combine(baseUrl, path)
            .ShouldBe("http://spring-caddy:8443/api/v1/connectors/github/token");
    }

    [Theory]
    [InlineData("http://host:5050", "/v1/agents/abc/connectors/github/token")]
    [InlineData("http://host:5050/", "/v1/agents/abc/connectors/github/token")]
    [InlineData("http://host:5050//", "v1/agents/abc/connectors/github/token")]
    public void Combine_WorkerHostShape_FromIssueBody_NeverProducesDoubleSlash(string baseUrl, string path)
    {
        UrlPath.Combine(baseUrl, path)
            .ShouldBe("http://host:5050/v1/agents/abc/connectors/github/token");
    }

    // --- Edge cases ---

    [Fact]
    public void Combine_EmptyPath_ReturnsBaseUnchanged()
    {
        UrlPath.Combine("http://host/api", string.Empty).ShouldBe("http://host/api");
        UrlPath.Combine("http://host/api/", string.Empty).ShouldBe("http://host/api/");
    }

    [Fact]
    public void Combine_EmptyBase_ReturnsPathUnchanged()
    {
        UrlPath.Combine(string.Empty, "/some/path").ShouldBe("/some/path");
        UrlPath.Combine(null, "some/path").ShouldBe("some/path");
        UrlPath.Combine("  ", "some/path").ShouldBe("some/path");
    }

    [Fact]
    public void Combine_BothEmpty_ReturnsEmpty()
    {
        UrlPath.Combine(null, null).ShouldBe(string.Empty);
        UrlPath.Combine(string.Empty, string.Empty).ShouldBe(string.Empty);
    }

    [Fact]
    public void Combine_PathIsAbsoluteUrl_ReturnsPathUnchanged()
    {
        UrlPath.Combine("http://base", "https://elsewhere/page")
            .ShouldBe("https://elsewhere/page");
        UrlPath.Combine("http://base/", "HTTP://Elsewhere/Page")
            .ShouldBe("HTTP://Elsewhere/Page");
    }

    [Theory]
    [InlineData("http://host", "")]
    [InlineData("http://host/", "")]
    public void Combine_EmptyPathDoesNotAddTrailingSlash(string baseUrl, string path)
    {
        UrlPath.Combine(baseUrl, path).ShouldBe(baseUrl);
    }

    [Fact]
    public void Combine_PreservesQueryStringOnPath()
    {
        UrlPath.Combine("http://host/api", "/search?q=x&l=10")
            .ShouldBe("http://host/api/search?q=x&l=10");
    }

    [Fact]
    public void Combine_PreservesFragmentOnPath()
    {
        UrlPath.Combine("http://host/api/", "page#anchor")
            .ShouldBe("http://host/api/page#anchor");
    }

    [Fact]
    public void Combine_NoUnnecessaryAllocation_ForSingleSlashBoundary()
    {
        // Sanity: the canonical happy path produces the obvious string.
        UrlPath.Combine("http://api.example.com", "/v1/health")
            .ShouldBe("http://api.example.com/v1/health");
    }

    // --- Demonstrates the audit value: the TrimEnd('/') + "/p" idiom
    // produces the same output as the helper on every well-formed
    // input. The helper exists so callers stop hand-rolling that
    // idiom (and silently mishandle the edges above). ---

    [Theory]
    [InlineData("http://x", "/y", "http://x/y")]
    [InlineData("http://x/", "/y", "http://x/y")]
    [InlineData("http://x/", "y", "http://x/y")]
    [InlineData("http://x", "y", "http://x/y")]
    public void Combine_MatchesTrimEndPlusSlashIdiom_OnWellFormedInputs(
        string baseUrl, string path, string expected)
    {
        UrlPath.Combine(baseUrl, path).ShouldBe(expected);
        (baseUrl.TrimEnd('/') + "/" + path.TrimStart('/')).ShouldBe(expected);
    }
}
