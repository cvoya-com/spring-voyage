// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

/// <summary>
/// Behavioural coverage for the #1629 PR6 Guid-or-name resolver:
/// direct-Guid short-circuit, 0-match, 1-match, n-match, and the
/// optional <c>--unit</c> context filter that narrows a name search to
/// one unit's membership. The resolver itself is internal so we hit it
/// through <see cref="InternalsVisibleTo"/>; test routes use a routing
/// HTTP handler that maps a path-method tuple to a canned JSON response
/// (the Kiota client does multiple round-trips per resolution call).
/// </summary>
public class CliResolverTests
{
    private const string BaseUrl = "http://localhost:5000";

    // Two stable Guids the tests reuse so the asserted output strings stay
    // grep-friendly. Both are the canonical no-dash 32-hex form (#1629 §3).
    private static readonly Guid AliceA = Guid.Parse("8c5fab2a8e7e4b9c92f1d8a3b4c5d6e7");
    private static readonly Guid AliceB = Guid.Parse("9d2e3f4a5b6c7d8e9f0a1b2c3d4e5f6a");
    private static readonly Guid Engineering = Guid.Parse("11111111111111111111111111111111");
    private static readonly Guid Design = Guid.Parse("22222222222222222222222222222222");

    [Fact]
    public async Task ResolveAgent_GuidNoDash_ShortCircuits_NoApiCall()
    {
        // Direct-Guid path must NOT round-trip through ListAgents — the
        // resolver returns the parsed value as-is. Fail fast if the
        // handler is invoked.
        var handler = new RoutingMockHandler();
        using var http = new HttpClient(handler);
        var client = new SpringApiClient(http, BaseUrl);
        var resolver = new CliResolver(client);

        var resolved = await resolver.ResolveAgentAsync(
            "8c5fab2a8e7e4b9c92f1d8a3b4c5d6e7",
            unitContext: null,
            ct: TestContext.Current.CancellationToken);

        resolved.ShouldBe(AliceA);
        handler.CallCount.ShouldBe(0);
    }

    [Fact]
    public async Task ResolveAgent_GuidDashed_ShortCircuits_NoApiCall()
    {
        // Lenient Guid parse: dashed form is still a Guid. Same
        // short-circuit as the no-dash case.
        var handler = new RoutingMockHandler();
        using var http = new HttpClient(handler);
        var client = new SpringApiClient(http, BaseUrl);
        var resolver = new CliResolver(client);

        var resolved = await resolver.ResolveAgentAsync(
            "8c5fab2a-8e7e-4b9c-92f1-d8a3b4c5d6e7",
            unitContext: null,
            ct: TestContext.Current.CancellationToken);

        resolved.ShouldBe(AliceA);
        handler.CallCount.ShouldBe(0);
    }

    [Fact]
    public async Task ResolveAgent_NameSingleMatch_ResolvesToGuid()
    {
        // 1-match: exactly one agent has display_name "Alice", so the
        // resolver returns that agent's Guid.
        var handler = new RoutingMockHandler();
        handler.OnGet("/api/v1/tenant/agents", AgentsListJson(
            (AliceA, "Alice", "engineering")));

        using var http = new HttpClient(handler);
        var client = new SpringApiClient(http, BaseUrl);
        var resolver = new CliResolver(client);

        var resolved = await resolver.ResolveAgentAsync(
            "Alice",
            unitContext: null,
            ct: TestContext.Current.CancellationToken);

        resolved.ShouldBe(AliceA);
    }

    [Fact]
    public async Task ResolveAgent_NameNoMatch_ThrowsZeroMatchException()
    {
        // 0-match: agent named "Bob" doesn't exist. Exception carries the
        // query and an empty candidate list so the printer can render
        // "No agent found …" to stderr.
        var handler = new RoutingMockHandler();
        handler.OnGet("/api/v1/tenant/agents", AgentsListJson(
            (AliceA, "Alice", "engineering")));

        using var http = new HttpClient(handler);
        var client = new SpringApiClient(http, BaseUrl);
        var resolver = new CliResolver(client);

        var ex = await Should.ThrowAsync<CliResolutionException>(async () =>
            await resolver.ResolveAgentAsync(
                "Bob",
                unitContext: null,
                ct: TestContext.Current.CancellationToken));

        ex.Kind.ShouldBe(CliEntityKind.Agent);
        ex.Query.ShouldBe("Bob");
        ex.Candidates.Count.ShouldBe(0);
        ex.IsAmbiguous.ShouldBeFalse();
    }

    [Fact]
    public async Task ResolveAgent_NameMultipleMatches_ThrowsAmbiguousException()
    {
        // n-match: two agents share display_name "Alice". The resolver
        // surfaces both as candidates with their Guids and parent context
        // so the disambiguation list can render usefully.
        var handler = new RoutingMockHandler();
        handler.OnGet("/api/v1/tenant/agents", AgentsListJson(
            (AliceA, "Alice", "engineering"),
            (AliceB, "Alice", "design")));

        using var http = new HttpClient(handler);
        var client = new SpringApiClient(http, BaseUrl);
        var resolver = new CliResolver(client);

        var ex = await Should.ThrowAsync<CliResolutionException>(async () =>
            await resolver.ResolveAgentAsync(
                "Alice",
                unitContext: null,
                ct: TestContext.Current.CancellationToken));

        ex.IsAmbiguous.ShouldBeTrue();
        ex.Candidates.Count.ShouldBe(2);
        ex.Candidates.ShouldContain(c => c.Id == AliceA);
        ex.Candidates.ShouldContain(c => c.Id == AliceB);
    }

    [Fact]
    public async Task ResolveAgent_NameWithUnitContext_NarrowsToMembers()
    {
        // Two Alices in the tenant — but only AliceA is a member of
        // Engineering. The --unit filter resolves the ambiguity to a
        // single match.
        var handler = new RoutingMockHandler();
        handler.OnGet("/api/v1/tenant/agents", AgentsListJson(
            (AliceA, "Alice", "engineering"),
            (AliceB, "Alice", "design")));
        handler.OnGet(
            $"/api/v1/tenant/units/{Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(Engineering)}/memberships",
            MembershipsListJson(AliceA));

        using var http = new HttpClient(handler);
        var client = new SpringApiClient(http, BaseUrl);
        var resolver = new CliResolver(client);

        var resolved = await resolver.ResolveAgentAsync(
            "Alice",
            unitContext: Engineering,
            ct: TestContext.Current.CancellationToken);

        resolved.ShouldBe(AliceA);
    }

    [Fact]
    public async Task ResolveAgent_NameWithUnitContext_NoMatchInUnit_ThrowsZeroMatch()
    {
        // Alice exists, but the requested unit has no member matching her.
        // Result is 0-match — caller can print "No agent found … in unit …".
        var handler = new RoutingMockHandler();
        handler.OnGet("/api/v1/tenant/agents", AgentsListJson(
            (AliceA, "Alice", "engineering")));
        handler.OnGet(
            $"/api/v1/tenant/units/{Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(Design)}/memberships",
            MembershipsListJson(/* nobody */));

        using var http = new HttpClient(handler);
        var client = new SpringApiClient(http, BaseUrl);
        var resolver = new CliResolver(client);

        var ex = await Should.ThrowAsync<CliResolutionException>(async () =>
            await resolver.ResolveAgentAsync(
                "Alice",
                unitContext: Design,
                ct: TestContext.Current.CancellationToken));

        ex.Candidates.Count.ShouldBe(0);
        ex.Context.ShouldBe(Design);
    }

    [Fact]
    public async Task ResolveUnit_GuidShortCircuits()
    {
        var handler = new RoutingMockHandler();
        using var http = new HttpClient(handler);
        var client = new SpringApiClient(http, BaseUrl);
        var resolver = new CliResolver(client);

        var resolved = await resolver.ResolveUnitAsync(
            "11111111111111111111111111111111",
            parentContext: null,
            ct: TestContext.Current.CancellationToken);

        resolved.ShouldBe(Engineering);
        handler.CallCount.ShouldBe(0);
    }

    [Fact]
    public async Task ResolveUnit_NameSingleMatch_ResolvesToGuid()
    {
        var handler = new RoutingMockHandler();
        handler.OnGet("/api/v1/tenant/units", UnitsListJson(
            (Engineering, "Engineering")));

        using var http = new HttpClient(handler);
        var client = new SpringApiClient(http, BaseUrl);
        var resolver = new CliResolver(client);

        var resolved = await resolver.ResolveUnitAsync(
            "Engineering",
            parentContext: null,
            ct: TestContext.Current.CancellationToken);

        resolved.ShouldBe(Engineering);
    }

    [Fact]
    public async Task ResolveUnit_NameMultipleMatches_ThrowsAmbiguousException()
    {
        var handler = new RoutingMockHandler();
        handler.OnGet("/api/v1/tenant/units", UnitsListJson(
            (Engineering, "Engineering"),
            (Design, "Engineering")));

        using var http = new HttpClient(handler);
        var client = new SpringApiClient(http, BaseUrl);
        var resolver = new CliResolver(client);

        var ex = await Should.ThrowAsync<CliResolutionException>(async () =>
            await resolver.ResolveUnitAsync(
                "Engineering",
                parentContext: null,
                ct: TestContext.Current.CancellationToken));

        ex.Kind.ShouldBe(CliEntityKind.Unit);
        ex.IsAmbiguous.ShouldBeTrue();
        ex.Candidates.Count.ShouldBe(2);
    }

    [Fact]
    public void Printer_ZeroMatch_RendersClearMessage()
    {
        // Output-shape contract: 0-match prints exactly one line; the
        // disambiguation list is suppressed.
        var ex = new CliResolutionException(
            CliEntityKind.Agent,
            "Bob",
            context: null,
            candidates: Array.Empty<CliResolutionCandidate>());

        using var sw = new StringWriter();
        CliResolutionPrinter.Write(sw, ex);

        var output = sw.ToString();
        output.ShouldContain("No agent found matching 'Bob'");
        output.ShouldNotContain("Re-run");
    }

    [Fact]
    public void Printer_ZeroMatch_WithUnitContext_RendersContextSuffix()
    {
        var ex = new CliResolutionException(
            CliEntityKind.Agent,
            "Bob",
            context: Engineering,
            candidates: Array.Empty<CliResolutionCandidate>());

        using var sw = new StringWriter();
        CliResolutionPrinter.Write(sw, ex);

        var output = sw.ToString();
        output.ShouldContain("No agent found matching 'Bob'");
        output.ShouldContain("in unit '" + Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(Engineering) + "'");
    }

    [Fact]
    public void Printer_NMatch_ListsCandidatesAndRerunHint()
    {
        // n-match must include every candidate's Guid + display name + an
        // example re-run line. Operators copy the Guid from the list and
        // re-run with -- so consistency of the output is the spec.
        var ex = new CliResolutionException(
            CliEntityKind.Agent,
            "Alice",
            context: null,
            candidates: new[]
            {
                new CliResolutionCandidate(AliceA, "Alice", "engineering/backend"),
                new CliResolutionCandidate(AliceB, "Alice", "design/research"),
            });

        using var sw = new StringWriter();
        CliResolutionPrinter.Write(sw, ex);

        var output = sw.ToString();
        output.ShouldContain("Multiple agents match 'Alice'");
        output.ShouldContain(Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(AliceA));
        output.ShouldContain(Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(AliceB));
        output.ShouldContain("engineering/backend");
        output.ShouldContain("design/research");
        output.ShouldContain("Re-run");
        output.ShouldContain($"spring agent show {Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(AliceA)}");
    }

    // --- helpers ----------------------------------------------------------

    private static string AgentsListJson(params (Guid Id, string DisplayName, string ParentUnit)[] rows)
    {
        // Note: tests emit Guid in dashed form because Kiota's deserialiser
        // (System.Text.Json's GetGuid()) requires that. The on-the-wire
        // form between server and CLI is no-dash — see #1629 §3 — but the
        // Kiota client tolerates both.
        var sb = new StringBuilder();
        sb.Append('[');
        for (var i = 0; i < rows.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append('{');
            sb.Append("\"id\":\"").Append(rows[i].Id.ToString("D")).Append('"').Append(',');
            sb.Append("\"name\":\"").Append(rows[i].DisplayName.ToLowerInvariant()).Append('"').Append(',');
            sb.Append("\"displayName\":\"").Append(rows[i].DisplayName).Append('"').Append(',');
            sb.Append("\"parentUnit\":\"").Append(rows[i].ParentUnit).Append('"');
            sb.Append('}');
        }
        sb.Append(']');
        return sb.ToString();
    }

    private static string UnitsListJson(params (Guid Id, string DisplayName)[] rows)
    {
        var sb = new StringBuilder();
        sb.Append('[');
        for (var i = 0; i < rows.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append('{');
            sb.Append("\"id\":\"").Append(rows[i].Id.ToString("D")).Append('"').Append(',');
            sb.Append("\"name\":\"").Append(rows[i].DisplayName.ToLowerInvariant()).Append('"').Append(',');
            sb.Append("\"displayName\":\"").Append(rows[i].DisplayName).Append('"');
            sb.Append('}');
        }
        sb.Append(']');
        return sb.ToString();
    }

    private static string MembershipsListJson(params Guid[] agentIds)
    {
        var sb = new StringBuilder();
        sb.Append('[');
        for (var i = 0; i < agentIds.Length; i++)
        {
            if (i > 0) sb.Append(',');
            // Wire shape mirrors the post-#1643 envelope: agentAddress
            // carries the canonical no-dash 32-hex form (no scheme prefix)
            // — the resolver also accepts agent://<id>.
            sb.Append('{');
            sb.Append("\"agentAddress\":\"").Append(agentIds[i].ToString("N")).Append('"');
            sb.Append('}');
        }
        sb.Append(']');
        return sb.ToString();
    }
}

/// <summary>
/// Lightweight HTTP routing handler for resolver tests: maps a
/// (method, absolute-path) tuple to a canned JSON response. Lets a single
/// resolver call (which fans out to multiple Kiota requests) be served
/// without the rigidity of <c>MockHttpMessageHandler</c>'s single-route
/// assertion.
/// </summary>
internal sealed class RoutingMockHandler : HttpMessageHandler
{
    private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _routes = new();

    public int CallCount { get; private set; }

    public List<string> Paths { get; } = new();

    public void OnGet(string path, string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        _routes[Key(HttpMethod.Get, path)] = (status, body);
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CallCount++;
        Paths.Add(request.RequestUri!.AbsolutePath);

        var key = Key(request.Method, request.RequestUri!.AbsolutePath);
        if (!_routes.TryGetValue(key, out var route))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent($"unrouted: {key}"),
            });
        }

        var response = new HttpResponseMessage(route.Status);
        if (!string.IsNullOrEmpty(route.Body))
        {
            response.Content = new StringContent(route.Body, Encoding.UTF8, "application/json");
        }
        return Task.FromResult(response);
    }

    private static string Key(HttpMethod method, string path) => $"{method}:{path}";
}