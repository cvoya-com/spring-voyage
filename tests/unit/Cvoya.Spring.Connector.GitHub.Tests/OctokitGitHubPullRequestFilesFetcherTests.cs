// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests;

using System.Net;
using System.Reflection;

using Cvoya.Spring.Connector.GitHub.Webhooks;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;
using NSubstitute.ExceptionExtensions;

using Octokit;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for <see cref="OctokitGitHubPullRequestFilesFetcher"/> covering the
/// short-paged / multi-paged / cap-exceeded / failure paths called out in
/// issue #2407. The fetcher is the producer side of the path-filter
/// expansion — it must page through GitHub's <c>/pulls/{n}/files</c>
/// reliably and fail open on transport / rate-limit blips.
/// </summary>
public class OctokitGitHubPullRequestFilesFetcherTests
{
    [Fact]
    public async Task FetchAsync_SinglePartialPage_ReturnsAllFiles()
    {
        var client = Substitute.For<IGitHubClient>();
        client.PullRequest.Files("o", "r", 1, Arg.Any<ApiOptions>())
            .Returns(BuildFiles("a.cs", "b.cs", "c.cs"));

        var fetcher = BuildFetcher(client);

        var result = await fetcher.FetchAsync("o", "r", 1, AppBinding(), TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result.ShouldBe(new[] { "a.cs", "b.cs", "c.cs" });
    }

    [Fact]
    public async Task FetchAsync_NoFiles_ReturnsEmptyList()
    {
        var client = Substitute.For<IGitHubClient>();
        client.PullRequest.Files("o", "r", 1, Arg.Any<ApiOptions>())
            .Returns(Array.Empty<PullRequestFile>());

        var fetcher = BuildFetcher(client);

        var result = await fetcher.FetchAsync("o", "r", 1, AppBinding(), TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task FetchAsync_MultiplePages_ConcatenatesUntilShortPage()
    {
        var client = Substitute.For<IGitHubClient>();
        var fullPage = BuildPageFiles(count: 100, prefix: "p1");
        var fullPage2 = BuildPageFiles(count: 100, prefix: "p2");
        var partial = BuildPageFiles(count: 17, prefix: "p3");

        ApiOptions[] captured = new ApiOptions[3];
        client.PullRequest.Files("o", "r", 7, Arg.Do<ApiOptions>(o =>
        {
            // Latch by StartPage rather than by call index to avoid ordering
            // flakiness if Substitute changes invocation visibility.
            captured[o.StartPage!.Value - 1] = o;
        })).Returns(_ =>
        {
            return (System.Collections.Generic.IReadOnlyList<PullRequestFile>)null!;
        });

        // Configure per-page returns by latching on StartPage.
        client.PullRequest.Files("o", "r", 7, Arg.Is<ApiOptions>(o => o.StartPage == 1))
            .Returns(fullPage);
        client.PullRequest.Files("o", "r", 7, Arg.Is<ApiOptions>(o => o.StartPage == 2))
            .Returns(fullPage2);
        client.PullRequest.Files("o", "r", 7, Arg.Is<ApiOptions>(o => o.StartPage == 3))
            .Returns(partial);

        var fetcher = BuildFetcher(client);

        var result = await fetcher.FetchAsync("o", "r", 7, AppBinding(), TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result.Count.ShouldBe(217);
        result[0].ShouldBe("p1-0");
        result[99].ShouldBe("p1-99");
        result[100].ShouldBe("p2-0");
        result[216].ShouldBe("p3-16");
    }

    [Fact]
    public async Task FetchAsync_CapExceeded_ReturnsEmptyToFailClosed()
    {
        // Every page is a full 100-file page. The fetcher must stop after
        // MaxPages (30) and surface an empty list so the path filter drops
        // the event (fail-closed, per the brief — degenerate PR hygiene).
        var client = Substitute.For<IGitHubClient>();
        client.PullRequest.Files("o", "r", 99, Arg.Any<ApiOptions>())
            .Returns(_ => BuildPageFiles(count: 100, prefix: "f"));

        var fetcher = BuildFetcher(client);

        var result = await fetcher.FetchAsync("o", "r", 99, AppBinding(), TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result.ShouldBeEmpty();
        // 30 fully-saturated pages requested.
        await client.PullRequest.Received(30).Files("o", "r", 99, Arg.Any<ApiOptions>());
    }

    [Fact]
    public async Task FetchAsync_NotFound_ReturnsEmptyList()
    {
        var client = Substitute.For<IGitHubClient>();
        client.PullRequest.Files("o", "r", 1, Arg.Any<ApiOptions>())
            .Throws(new NotFoundException("gone", HttpStatusCode.NotFound));

        var fetcher = BuildFetcher(client);

        var result = await fetcher.FetchAsync("o", "r", 1, AppBinding(), TestContext.Current.CancellationToken);

        // PR vanished — path filter sees no files and drops the event.
        result.ShouldNotBeNull();
        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task FetchAsync_TransportFailure_ReturnsNullToFailOpen()
    {
        var client = Substitute.For<IGitHubClient>();
        client.PullRequest.Files("o", "r", 1, Arg.Any<ApiOptions>())
            .Throws(new HttpRequestException("network down"));

        var fetcher = BuildFetcher(client);

        var result = await fetcher.FetchAsync("o", "r", 1, AppBinding(), TestContext.Current.CancellationToken);

        // Transport blip — fail open so the operator's subscription isn't
        // silently broken by a transient GitHub outage.
        result.ShouldBeNull();
    }

    [Fact]
    public async Task FetchAsync_ServerError_ReturnsNullToFailOpen()
    {
        var client = Substitute.For<IGitHubClient>();
        client.PullRequest.Files("o", "r", 1, Arg.Any<ApiOptions>())
            .Throws(new ApiException("boom", HttpStatusCode.InternalServerError));

        var fetcher = BuildFetcher(client);

        var result = await fetcher.FetchAsync("o", "r", 1, AppBinding(), TestContext.Current.CancellationToken);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task FetchAsync_RequestsHundredPerPage()
    {
        ApiOptions? captured = null;
        var client = Substitute.For<IGitHubClient>();
        client.PullRequest.Files("o", "r", 1, Arg.Do<ApiOptions>(o => captured = o))
            .Returns(BuildFiles("a"));

        var fetcher = BuildFetcher(client);

        await fetcher.FetchAsync("o", "r", 1, AppBinding(), TestContext.Current.CancellationToken);

        captured.ShouldNotBeNull();
        captured!.PageSize.ShouldBe(100);
        captured.PageCount.ShouldBe(1);
        captured.StartPage.ShouldBe(1);
    }

    [Fact]
    public async Task FetchAsync_ConnectorThrows_FailsOpen()
    {
        var fetcher = new OctokitGitHubPullRequestFilesFetcher(
            connectorAccessor: () => throw new InvalidOperationException("no auth"),
            loggerFactory: NullLoggerFactory.Instance);

        var result = await fetcher.FetchAsync("o", "r", 1, AppBinding(), TestContext.Current.CancellationToken);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task FetchAsync_AppBinding_AuthenticatesThroughResolver()
    {
        // ADR-0047 §6: the fetcher hands the binding to the connector,
        // which dispatches to the resolver. The resolver behind the
        // factory picks App vs PAT; the fetcher itself never branches on
        // auth type — that is the resolver's job.
        var client = Substitute.For<IGitHubClient>();
        client.PullRequest.Files("o", "r", 1, Arg.Any<ApiOptions>())
            .Returns(Array.Empty<PullRequestFile>());

        var connector = Substitute.For<IGitHubConnector>();
        connector.CreateAuthenticatedClientForBindingAsync(
                Arg.Any<UnitGitHubConfig>(), Arg.Any<CancellationToken>())
            .Returns(client);

        var fetcher = new OctokitGitHubPullRequestFilesFetcher(
            connectorAccessor: () => connector,
            loggerFactory: NullLoggerFactory.Instance);

        var binding = new UnitGitHubConfig("o/r", AppInstallationId: 9001);

        await fetcher.FetchAsync("o", "r", 1, binding, TestContext.Current.CancellationToken);

        await connector.Received(1)
            .CreateAuthenticatedClientForBindingAsync(
                Arg.Is<UnitGitHubConfig>(c => c.AppInstallationId == 9001),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FetchAsync_PatBinding_AuthenticatesThroughResolver()
    {
        // PAT-bound binding: same call shape, different field. The
        // connector's factory carries the binding into the resolver,
        // which then walks the tenant secret store.
        var client = Substitute.For<IGitHubClient>();
        client.PullRequest.Files("o", "r", 1, Arg.Any<ApiOptions>())
            .Returns(Array.Empty<PullRequestFile>());

        var connector = Substitute.For<IGitHubConnector>();
        connector.CreateAuthenticatedClientForBindingAsync(
                Arg.Any<UnitGitHubConfig>(), Arg.Any<CancellationToken>())
            .Returns(client);

        var fetcher = new OctokitGitHubPullRequestFilesFetcher(
            connectorAccessor: () => connector,
            loggerFactory: NullLoggerFactory.Instance);

        var binding = new UnitGitHubConfig("o/r", PatSecretName: "binding/abc/github/pat");

        await fetcher.FetchAsync("o", "r", 1, binding, TestContext.Current.CancellationToken);

        await connector.Received(1)
            .CreateAuthenticatedClientForBindingAsync(
                Arg.Is<UnitGitHubConfig>(c => c.PatSecretName == "binding/abc/github/pat"),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FetchAsync_AuthMissingFromResolver_FailsOpen()
    {
        // When the resolver raises GitHubBindingAuthMissing — secret
        // gone, installation token mint rejected — the fetcher must
        // surface null so the caller fails open consistent with
        // transport-failure semantics. Closing on auth-missing would
        // silently break webhook routing for legitimate operators who
        // are just rotating credentials.
        var connector = Substitute.For<IGitHubConnector>();
        connector.CreateAuthenticatedClientForBindingAsync(
                Arg.Any<UnitGitHubConfig>(), Arg.Any<CancellationToken>())
            .Throws(new Cvoya.Spring.Connector.GitHub.Auth.GitHubBindingAuthMissingException("secret gone"));

        var fetcher = new OctokitGitHubPullRequestFilesFetcher(
            connectorAccessor: () => connector,
            loggerFactory: NullLoggerFactory.Instance);

        var result = await fetcher.FetchAsync(
            "o", "r", 1, AppBinding(), TestContext.Current.CancellationToken);

        result.ShouldBeNull();
    }

    private static OctokitGitHubPullRequestFilesFetcher BuildFetcher(IGitHubClient client)
    {
        var connector = Substitute.For<IGitHubConnector>();
        connector.CreateAuthenticatedClientForBindingAsync(
                Arg.Any<UnitGitHubConfig>(), Arg.Any<CancellationToken>())
            .Returns(client);
        return new OctokitGitHubPullRequestFilesFetcher(
            connectorAccessor: () => connector,
            loggerFactory: NullLoggerFactory.Instance);
    }

    private static UnitGitHubConfig AppBinding() =>
        new("o/r", AppInstallationId: 4242);

    private static PullRequestFile[] BuildFiles(params string[] names) =>
        names.Select(n => CreatePullRequestFile(n)).ToArray();

    private static PullRequestFile[] BuildPageFiles(int count, string prefix)
    {
        var arr = new PullRequestFile[count];
        for (var i = 0; i < count; i++)
        {
            arr[i] = CreatePullRequestFile($"{prefix}-{i}");
        }
        return arr;
    }

    /// <summary>
    /// Constructs a <see cref="PullRequestFile"/> by reflection so the test
    /// doesn't depend on Octokit's specific constructor signature (it has
    /// internal setters and a long positional ctor). Mirrors the pattern
    /// in <see cref="PrTestHelpers"/>.
    /// </summary>
    private static PullRequestFile CreatePullRequestFile(string filename)
    {
        var ctor = typeof(PullRequestFile)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .OrderByDescending(c => c.GetParameters().Length)
            .First();

        var parameters = ctor.GetParameters();
        var args = new object?[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            var p = parameters[i];
            args[i] = p.Name switch
            {
                "fileName" => filename,
                "filename" => filename,
                _ => DefaultValue(p.ParameterType),
            };
        }
        return (PullRequestFile)ctor.Invoke(args);
    }

    private static object? DefaultValue(Type t) =>
        t.IsValueType ? Activator.CreateInstance(t) : null;
}
