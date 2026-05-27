// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Tests;

using System;
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
    public async Task ResolveHumanRefAsync_DisplayNameMultipleMatches_ThrowsWithDisambiguationHint()
    {
        var ct = TestContext.Current.CancellationToken;
        var firstId = Guid.Parse("aaaaaaaa-1111-1111-1111-000000000001");
        var secondId = Guid.Parse("aaaaaaaa-1111-1111-1111-000000000002");
        var responseBody =
            $$"""
            [
              {"humanId":"{{firstId:D}}","displayName":"Bob","isPrimary":true,"memberships":[]},
              {"humanId":"{{secondId:D}}","displayName":"Bob","isPrimary":false,"memberships":[]}
            ]
            """;
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/tenant/users/me/humans",
            expectedMethod: HttpMethod.Get,
            responseBody: responseBody);
        var client = new SpringApiClient(new HttpClient(handler), BaseUrl);

        var ex = await Should.ThrowAsync<CliRefResolutionException>(
            () => RefResolver.ResolveHumanRefAsync(client, "Bob", "--as", ct));

        ex.Message.ShouldContain("matches more than one Hat");
        ex.Message.ShouldContain("disambiguate");
        ex.Message.ShouldContain(firstId.ToString("N"));
        ex.Message.ShouldContain(secondId.ToString("N"));
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
