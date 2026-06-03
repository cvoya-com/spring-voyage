// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Tests;

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Cli.Utilities;
using Cvoya.Spring.Core.Tenancy;

using Shouldly;

using Xunit;

/// <summary>
/// Parser-level tests for <see cref="RefResolver"/> — the shared
/// resolver behind the <c>spring message send --as</c>, <c>spring user
/// identity set-primary</c>, <c>spring unit members humans add --as</c>,
/// and <c>spring package install --as-human</c> ref shapes (ADR-0062
/// § 6 / #2822, #2827).
/// </summary>
public class RefResolverTests
{
    private const string BaseUrl = "http://localhost:5000";

    // ── ResolveHumanRefAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task ResolveHumanRefAsync_BareGuid_PassesThroughWithoutLookup()
    {
        // Short-circuit: a parseable Guid skips the /me/humans round-
        // trip so scripted invocations stay fast.
        var ct = TestContext.Current.CancellationToken;
        var input = "11111111-2222-3333-4444-555555555555";
        var handler = new MockHttpMessageHandler(
            expectedPath: "/should-not-be-called",
            expectedMethod: HttpMethod.Get,
            responseBody: "{}");
        var client = new SpringApiClient(new HttpClient(handler), BaseUrl);

        var resolved = await RefResolver.ResolveHumanRefAsync(client, input, "--as", ct);

        resolved.ShouldBe(Guid.Parse(input));
        handler.WasCalled.ShouldBeFalse();
    }

    [Fact]
    public async Task ResolveHumanRefAsync_NoDashGuid_PassesThroughWithoutLookup()
    {
        var ct = TestContext.Current.CancellationToken;
        var hex = "111111112222333344445555555555aa";
        var handler = new MockHttpMessageHandler(
            expectedPath: "/should-not-be-called",
            expectedMethod: HttpMethod.Get,
            responseBody: "{}");
        var client = new SpringApiClient(new HttpClient(handler), BaseUrl);

        var resolved = await RefResolver.ResolveHumanRefAsync(client, hex, "--as", ct);

        resolved.ShouldBe(Guid.Parse(hex));
        handler.WasCalled.ShouldBeFalse();
    }

    [Fact]
    public async Task ResolveHumanRefAsync_DisplayNameSingleMatch_ResolvesToHatId()
    {
        // ADR-0062 § 6 / #2827: a display-name input matches case-
        // insensitively against the calling caller's bound-Hat set.
        var ct = TestContext.Current.CancellationToken;
        var targetId = Guid.Parse("aaaaaaaa-1111-1111-1111-000000000001");
        var responseBody =
            $$"""
            [
              {"humanId":"{{targetId:D}}","displayName":"Bob","isPrimary":true,"memberships":[]}
            ]
            """;
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/tenant/users/me/humans",
            expectedMethod: HttpMethod.Get,
            responseBody: responseBody);
        var client = new SpringApiClient(new HttpClient(handler), BaseUrl);

        var resolved = await RefResolver.ResolveHumanRefAsync(client, "bob", "--as", ct);

        resolved.ShouldBe(targetId);
    }

    [Fact]
    public async Task ResolveHumanRefAsync_DisplayNameZeroMatches_ThrowsWithBoundSetHint()
    {
        var ct = TestContext.Current.CancellationToken;
        var responseBody = "[]";
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/tenant/users/me/humans",
            expectedMethod: HttpMethod.Get,
            responseBody: responseBody);
        var client = new SpringApiClient(new HttpClient(handler), BaseUrl);

        var ex = await Should.ThrowAsync<CliRefResolutionException>(
            () => RefResolver.ResolveHumanRefAsync(client, "Nobody", "--as", ct));

        ex.Message.ShouldContain("No bound Hat matches");
        ex.Message.ShouldContain("Nobody");
        ex.Message.ShouldContain("spring user identity list");
    }

    [Fact]
    public async Task ResolveHumanRefAsync_DisplayNameMultipleMatches_NonTty_ThrowsStructuredHint()
    {
        // #2829: with stdin redirected (CI / piped input / scripted
        // invocation) ambiguous matches surface as a structured error
        // listing the candidate disambiguated labels — never an
        // interactive prompt the script can't answer.
        var ct = TestContext.Current.CancellationToken;
        var firstId = Guid.Parse("aaaaaaaa-1111-1111-1111-000000000001");
        var secondId = Guid.Parse("aaaaaaaa-1111-1111-1111-000000000002");
        var responseBody =
            $$"""
            [
              {"humanId":"{{firstId:D}}","displayName":"Bob","disambiguatedLabel":"Bob (Magazine)","isPrimary":true,"memberships":[]},
              {"humanId":"{{secondId:D}}","displayName":"Bob","disambiguatedLabel":"Bob (Newsletter)","isPrimary":false,"memberships":[]}
            ]
            """;
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/tenant/users/me/humans",
            expectedMethod: HttpMethod.Get,
            responseBody: responseBody);
        var client = new SpringApiClient(new HttpClient(handler), BaseUrl);

        var ex = await Should.ThrowAsync<CliRefResolutionException>(
            () => RefResolver.ResolveHumanRefAsync(
                client,
                input: "Bob",
                flagDescription: "--as",
                targetRecipient: null,
                stdin: new StringReader(string.Empty),
                stdout: new StringWriter(),
                isInputRedirected: true,
                ct));

        ex.Message.ShouldContain("matched multiple Hats");
        ex.Message.ShouldContain("--as \"Bob (Magazine)\"");
        ex.Message.ShouldContain("--as \"Bob (Newsletter)\"");
    }

    [Fact]
    public async Task ResolveHumanRefAsync_DisplayNameMultipleMatches_Tty_PromptsAndResolvesChoice()
    {
        // #2829: on a real TTY the resolver renders a numbered list
        // using the server's disambiguatedLabel values and resolves
        // the chosen index to the corresponding Hat's Guid.
        var ct = TestContext.Current.CancellationToken;
        var firstId = Guid.Parse("aaaaaaaa-1111-1111-1111-000000000001");
        var secondId = Guid.Parse("aaaaaaaa-1111-1111-1111-000000000002");
        var responseBody =
            $$"""
            [
              {"humanId":"{{firstId:D}}","displayName":"Bob","disambiguatedLabel":"Bob (Magazine)","isPrimary":true,"memberships":[]},
              {"humanId":"{{secondId:D}}","displayName":"Bob","disambiguatedLabel":"Bob (Newsletter)","isPrimary":false,"memberships":[]}
            ]
            """;
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/tenant/users/me/humans",
            expectedMethod: HttpMethod.Get,
            responseBody: responseBody);
        var client = new SpringApiClient(new HttpClient(handler), BaseUrl);

        // Operator picks "2" → resolves to secondId.
        var stdin = new StringReader("2\n");
        var stdout = new StringWriter();

        var resolved = await RefResolver.ResolveHumanRefAsync(
            client,
            input: "Bob",
            flagDescription: "--as",
            targetRecipient: null,
            stdin: stdin,
            stdout: stdout,
            isInputRedirected: false,
            ct);

        resolved.ShouldBe(secondId);

        var rendered = stdout.ToString();
        rendered.ShouldContain("which \"Bob\"?");
        rendered.ShouldContain("1. Bob (Magazine)");
        rendered.ShouldContain("2. Bob (Newsletter)");
        rendered.ShouldContain("pick one [1-2]:");
    }

    [Fact]
    public async Task ResolveHumanRefAsync_DisplayNameMultipleMatches_Tty_InvalidInputReprompts()
    {
        // Invalid input must NOT abort — the operator gets a hint and
        // the prompt re-fires. Only EOF / explicit cancel terminates
        // the loop.
        var ct = TestContext.Current.CancellationToken;
        var firstId = Guid.Parse("aaaaaaaa-1111-1111-1111-000000000001");
        var secondId = Guid.Parse("aaaaaaaa-1111-1111-1111-000000000002");
        var responseBody =
            $$"""
            [
              {"humanId":"{{firstId:D}}","displayName":"Bob","disambiguatedLabel":"Bob (Magazine)","isPrimary":true,"memberships":[]},
              {"humanId":"{{secondId:D}}","displayName":"Bob","disambiguatedLabel":"Bob (Newsletter)","isPrimary":false,"memberships":[]}
            ]
            """;
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/tenant/users/me/humans",
            expectedMethod: HttpMethod.Get,
            responseBody: responseBody);
        var client = new SpringApiClient(new HttpClient(handler), BaseUrl);

        // "foo" (not a number) → reprompt; "9" (out of range) → reprompt;
        // "1" → resolve.
        var stdin = new StringReader("foo\n9\n1\n");
        var stdout = new StringWriter();

        var resolved = await RefResolver.ResolveHumanRefAsync(
            client,
            input: "Bob",
            flagDescription: "--as",
            targetRecipient: null,
            stdin: stdin,
            stdout: stdout,
            isInputRedirected: false,
            ct);

        resolved.ShouldBe(firstId);

        var rendered = stdout.ToString();
        rendered.ShouldContain("invalid choice 'foo'");
        rendered.ShouldContain("invalid choice '9'");
    }

    [Fact]
    public async Task ResolveHumanRefAsync_DisplayNameMultipleMatches_Tty_EofAborts()
    {
        // EOF on stdin (operator pressed ^D / the input stream closed
        // without a pick) aborts with a non-zero exit so scripts don't
        // silently pick a Hat.
        var ct = TestContext.Current.CancellationToken;
        var firstId = Guid.Parse("aaaaaaaa-1111-1111-1111-000000000001");
        var secondId = Guid.Parse("aaaaaaaa-1111-1111-1111-000000000002");
        var responseBody =
            $$"""
            [
              {"humanId":"{{firstId:D}}","displayName":"Bob","disambiguatedLabel":"Bob (Magazine)","isPrimary":true,"memberships":[]},
              {"humanId":"{{secondId:D}}","displayName":"Bob","disambiguatedLabel":"Bob (Newsletter)","isPrimary":false,"memberships":[]}
            ]
            """;
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/tenant/users/me/humans",
            expectedMethod: HttpMethod.Get,
            responseBody: responseBody);
        var client = new SpringApiClient(new HttpClient(handler), BaseUrl);

        var stdin = new StringReader(string.Empty); // immediate EOF
        var stdout = new StringWriter();

        var ex = await Should.ThrowAsync<CliRefResolutionException>(
            () => RefResolver.ResolveHumanRefAsync(
                client,
                input: "Bob",
                flagDescription: "--as",
                targetRecipient: null,
                stdin: stdin,
                stdout: stdout,
                isInputRedirected: false,
                ct));

        ex.Message.ShouldContain("Aborted");
        ex.Message.ShouldContain("Bob");
    }

    [Fact]
    public async Task ResolveHumanRefAsync_DisambiguatedLabelMatch_ResolvesWithoutPrompt()
    {
        // #2829: typing the exact server-rendered disambiguated label
        // resolves to a single Hat without an ambiguity prompt — even
        // when other bound Hats share the raw display name.
        var ct = TestContext.Current.CancellationToken;
        var firstId = Guid.Parse("aaaaaaaa-1111-1111-1111-000000000001");
        var secondId = Guid.Parse("aaaaaaaa-1111-1111-1111-000000000002");
        var responseBody =
            $$"""
            [
              {"humanId":"{{firstId:D}}","displayName":"Bob","disambiguatedLabel":"Bob (Magazine)","isPrimary":true,"memberships":[]},
              {"humanId":"{{secondId:D}}","displayName":"Bob","disambiguatedLabel":"Bob (Newsletter)","isPrimary":false,"memberships":[]}
            ]
            """;
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/tenant/users/me/humans",
            expectedMethod: HttpMethod.Get,
            responseBody: responseBody);
        var client = new SpringApiClient(new HttpClient(handler), BaseUrl);

        var resolved = await RefResolver.ResolveHumanRefAsync(
            client, "Bob (Newsletter)", "--as", ct);

        resolved.ShouldBe(secondId);
    }

    [Fact]
    public async Task ResolveHumanRefAsync_ScopedToRecipient_SendsRecipientQueryAndResolves()
    {
        // #2972: passing a target recipient scopes the /me/humans lookup to
        // the Hats that can reach it; the CLI forwards the recipient as a
        // `recipient=` query parameter so the server filters server-side.
        var ct = TestContext.Current.CancellationToken;
        var targetId = Guid.Parse("aaaaaaaa-1111-1111-1111-0000000000a1");
        var responseBody =
            $$"""
            [
              {"humanId":"{{targetId:D}}","displayName":"Bob","isPrimary":true,"memberships":[]}
            ]
            """;
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/tenant/users/me/humans",
            expectedMethod: HttpMethod.Get,
            responseBody: responseBody,
            validateQuery: q => q.ShouldContain("recipient="));
        var client = new SpringApiClient(new HttpClient(handler), BaseUrl);

        var resolved = await RefResolver.ResolveHumanRefAsync(
            client,
            "Bob",
            "--as",
            "unit:111111112222333344445555555555aa",
            ct);

        resolved.ShouldBe(targetId);
        handler.WasCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task ResolveHumanRefAsync_ScopedZeroMatches_ThrowsReachabilityHint()
    {
        // #2972: when scoped and nothing matches, the error steers the
        // operator toward the reachability rule rather than "no such Hat".
        var ct = TestContext.Current.CancellationToken;
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/tenant/users/me/humans",
            expectedMethod: HttpMethod.Get,
            responseBody: "[]");
        var client = new SpringApiClient(new HttpClient(handler), BaseUrl);

        var ex = await Should.ThrowAsync<CliRefResolutionException>(
            () => RefResolver.ResolveHumanRefAsync(
                client,
                "Bob",
                "--as",
                "agent:111111112222333344445555555555aa",
                ct));

        ex.Message.ShouldContain("Hat you can use to message");
        ex.Message.ShouldContain("human member of the unit");
    }

    // ── ResolveTenantUserRefAsync ────────────────────────────────────────────

    [Fact]
    public async Task ResolveTenantUserRefAsync_BareGuid_PassesThroughWithoutLookup()
    {
        var ct = TestContext.Current.CancellationToken;
        var input = "33333333-2222-3333-4444-555555555555";
        var handler = new MockHttpMessageHandler(
            expectedPath: "/should-not-be-called",
            expectedMethod: HttpMethod.Get,
            responseBody: "{}");
        var client = new SpringApiClient(new HttpClient(handler), BaseUrl);

        var resolved = await RefResolver.ResolveTenantUserRefAsync(client, input, "--as", ct);

        resolved.ShouldBe(Guid.Parse(input));
        handler.WasCalled.ShouldBeFalse();
    }

    [Fact]
    public async Task ResolveTenantUserRefAsync_Me_ResolvesToOssOperatorWithoutLookup()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new MockHttpMessageHandler(
            expectedPath: "/should-not-be-called",
            expectedMethod: HttpMethod.Get,
            responseBody: "{}");
        var client = new SpringApiClient(new HttpClient(handler), BaseUrl);

        var resolved = await RefResolver.ResolveTenantUserRefAsync(client, "me", "--as", ct);

        resolved.ShouldBe(OssTenantUserIds.Operator);
        handler.WasCalled.ShouldBeFalse();
    }

    [Fact]
    public async Task ResolveTenantUserRefAsync_OAuthSubjectMatch_ResolvesToTenantUserId()
    {
        // ADR-0062 § 6 / #2827: a non-Guid non-`me` input is resolved
        // server-side via GET /api/v1/tenant/users?authSubject=<...>.
        var ct = TestContext.Current.CancellationToken;
        var targetId = Guid.Parse("bbbbbbbb-2222-2222-2222-000000000099");
        var responseBody =
            $$"""
            {
              "id":"{{targetId:D}}",
              "authSubject":"alice@example.com",
              "displayName":"Alice",
              "description":null,
              "createdAt":null,
              "updatedAt":null
            }
            """;
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/tenant/users",
            expectedMethod: HttpMethod.Get,
            responseBody: responseBody,
            validateQuery: q => q.ShouldContain("authSubject=alice"));
        var client = new SpringApiClient(new HttpClient(handler), BaseUrl);

        var resolved = await RefResolver.ResolveTenantUserRefAsync(
            client, "alice@example.com", "--as", ct);

        resolved.ShouldBe(targetId);
    }

    [Fact]
    public async Task ResolveTenantUserRefAsync_OAuthSubjectNotFound_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/tenant/users",
            expectedMethod: HttpMethod.Get,
            responseBody: "",
            returnStatusCode: HttpStatusCode.NotFound);
        var client = new SpringApiClient(new HttpClient(handler), BaseUrl);

        var ex = await Should.ThrowAsync<CliRefResolutionException>(
            () => RefResolver.ResolveTenantUserRefAsync(
                client, "nobody@example.com", "--as", ct));

        ex.Message.ShouldContain("nobody@example.com");
        ex.Message.ShouldContain("auth subject");
    }
}
