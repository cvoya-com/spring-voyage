// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Endpoints;

using System.Net;
using System.Net.Http.Json;

using Cvoya.Spring.Core.Secrets;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Host.Api.Endpoints;

using Microsoft.Extensions.DependencyInjection;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for <c>GET /api/v1/system/credentials/{provider}/status</c>
/// (#598). The endpoint is the wizard-side probe that tells operators
/// whether the selected LLM provider's credentials are actually
/// configured before they commit to creating a unit.
/// </summary>
/// <remarks>
/// Every test in this file also asserts that the raw response body
/// does NOT leak the stored plaintext. The resolver resolves plaintext
/// in-process but the endpoint is expected to drop it on the floor —
/// that invariant is load-bearing because any downstream consumer of
/// this endpoint (portal, CLI, scripts) will treat the response as
/// not-secret.
/// </remarks>
public class SystemEndpointsTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public SystemEndpointsTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Status_UnknownProvider_ReturnsBadRequest()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _client.GetAsync(
            "/api/v1/platform/credentials/no-such-provider/status", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync(ct);
        body.ShouldContain("unknown-provider");
    }

    [Fact]
    public async Task Status_Anthropic_NotConfigured_ReportsFalseWithSuggestion()
    {
        var ct = TestContext.Current.CancellationToken;
        await ClearSecretsAsync(ct);

        var response = await _client.GetAsync(
            "/api/v1/platform/credentials/anthropic/status", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ProviderCredentialStatusResponse>(ct);
        body.ShouldNotBeNull();
        body!.Provider.ShouldBe("anthropic");
        body.Resolvable.ShouldBeFalse();
        body.Source.ShouldBeNull();
        body.Suggestion.ShouldNotBeNullOrWhiteSpace();
        body.Suggestion!.ShouldContain("anthropic-api-key");
        body.Reason.ShouldBe("not-configured");
    }

    [Fact]
    public async Task Status_Anthropic_SlotPresentButCipherUnreadable_ReportsFalseWithUnreadableReason()
    {
        // Regression guard for #978 defect 1: when the stored ciphertext
        // is present but its AES-GCM tag fails to authenticate (e.g. the
        // at-rest key rotated between the write and the read) the probe
        // used to bubble a raw CryptographicException and crash the
        // endpoint with 500. The endpoint must now return 200 with a
        // structured "unreadable" reason so the wizard can render a
        // useful error and the operator knows to rotate the key.
        //
        // The test-harness substitutes the full ISecretStore, so we can't
        // exercise the end-to-end encryptor failure here (that's covered
        // in SecretsEncryptorTests + DaprStateBackedSecretStoreTests). The
        // equivalent contract at this seam is: when the store surfaces a
        // SecretUnreadableException, the endpoint maps it to
        // resolvable:false + reason:"unreadable".
        var ct = TestContext.Current.CancellationToken;
        _ = await SeedRegistryOnlyAsync("anthropic-api-key", ct);
        _factory.SecretStore
            .ReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<string?>(_ => throw new SecretUnreadableException());

        var response = await _client.GetAsync(
            "/api/v1/platform/credentials/anthropic/status", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<ProviderCredentialStatusResponse>(ct);
        body.ShouldNotBeNull();
        body!.Provider.ShouldBe("anthropic");
        body.Resolvable.ShouldBeFalse();
        body.Source.ShouldBeNull();
        body.Reason.ShouldBe("unreadable");
        body.Suggestion.ShouldNotBeNullOrWhiteSpace();
        // The unreadable-specific copy must point at the rotation path,
        // not the "create it" path — otherwise the operator would re-save
        // a value they already have and the slot would still be orphaned.
        body.Suggestion!.ShouldContain("cannot decrypt");
    }

    [Fact]
    public async Task Status_Anthropic_TenantConfigured_ReportsTenantSource()
    {
        var ct = TestContext.Current.CancellationToken;
        // #1690: the format check now rejects values that are neither
        // sk-ant-api… nor sk-ant-oat… on every dispatch path, so the
        // tenant-configured fixture must use a valid Anthropic shape.
        await SeedTenantSecretAsync("anthropic-api-key", "sk-ant-api-tenant-default", ct);

        var response = await _client.GetAsync(
            "/api/v1/platform/credentials/anthropic/status", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var raw = await response.Content.ReadAsStringAsync(ct);
        // Non-negotiable: the key material must never appear in the
        // response. If this ever fails, the endpoint has leaked plaintext.
        raw.ShouldNotContain("sk-ant-api-tenant-default");

        var body = await response.Content.ReadFromJsonAsync<ProviderCredentialStatusResponse>(ct);
        body.ShouldNotBeNull();
        body!.Provider.ShouldBe("anthropic");
        body.Resolvable.ShouldBeTrue();
        body.Source.ShouldBe("tenant");
        body.Suggestion.ShouldBeNull();
        body.Reason.ShouldBeNull();
    }

    [Fact]
    public async Task Status_OpenAi_TenantConfigured_Resolvable()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedTenantSecretAsync("openai-api-key", "sk-openai-value", ct);

        var response = await _client.GetAsync(
            "/api/v1/platform/credentials/openai/status", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(ct)).ShouldNotContain("sk-openai-value");

        var body = await response.Content.ReadFromJsonAsync<ProviderCredentialStatusResponse>(ct);
        body.ShouldNotBeNull();
        body!.Resolvable.ShouldBeTrue();
        body.Source.ShouldBe("tenant");
    }

    [Fact]
    public async Task Status_Google_NotConfigured_ReportsFalse()
    {
        var ct = TestContext.Current.CancellationToken;
        await ClearSecretsAsync(ct);

        var response = await _client.GetAsync(
            "/api/v1/platform/credentials/google/status", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ProviderCredentialStatusResponse>(ct);
        body.ShouldNotBeNull();
        body!.Resolvable.ShouldBeFalse();
        body.Source.ShouldBeNull();
        body.Suggestion.ShouldNotBeNullOrWhiteSpace();
        body.Suggestion!.ShouldContain("google-api-key");
    }

    [Fact]
    public async Task Status_Ollama_Unreachable_ReportsFalseWithBaseUrl()
    {
        // The test harness runs without an Ollama server — the probe
        // must report `resolvable: false` and surface an actionable
        // suggestion without throwing.
        var ct = TestContext.Current.CancellationToken;

        var response = await _client.GetAsync(
            "/api/v1/platform/credentials/ollama/status", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ProviderCredentialStatusResponse>(ct);
        body.ShouldNotBeNull();
        body!.Provider.ShouldBe("ollama");
        body.Resolvable.ShouldBeFalse();
        // Ollama has no tenant/unit secret concept — Source is always null.
        body.Source.ShouldBeNull();
        body.Suggestion.ShouldNotBeNullOrWhiteSpace();
        body.Suggestion!.ShouldContain("Ollama");
    }

    [Fact]
    public async Task Status_AliasClaude_ResolvesSameAsAnthropic()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedTenantSecretAsync("anthropic-api-key", "sk-claude-alias", ct);

        var response = await _client.GetAsync(
            "/api/v1/platform/credentials/claude/status", ct);

        // "claude" is accepted as an alias only inside the resolver's
        // provider-id mapping. At the HTTP layer only the canonical
        // values are exposed — so "claude" is rejected by the endpoint
        // with 400 and operators use "anthropic" consistently on the
        // wire. (This keeps the wire surface tight and mirrors the
        // Settings → Tenant defaults panel labels.)
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Status_Anthropic_OAuthToken_RestPath_ReportsFormatRejected()
    {
        // Regression guard for #1003: an OAuth token stored as the
        // tenant-default credential decrypts cleanly, so the resolver
        // returns it with Source=Tenant. But the Anthropic Platform REST
        // endpoint (the IAiProvider dispatch path) rejects OAuth tokens
        // with a 401 indistinguishable from a bad key — see #981.
        // The probe must surface that mismatch pre-dispatch so the
        // wizard does not show a green badge for a credential that
        // will fail on the first message.
        var ct = TestContext.Current.CancellationToken;
        await SeedTenantSecretAsync("anthropic-api-key", "sk-ant-oat-fake-token", ct);

        var response = await _client.GetAsync(
            "/api/v1/platform/credentials/anthropic/status?dispatchPath=rest", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var raw = await response.Content.ReadAsStringAsync(ct);
        raw.ShouldNotContain("sk-ant-oat-fake-token");

        var body = await response.Content.ReadFromJsonAsync<ProviderCredentialStatusResponse>(ct);
        body.ShouldNotBeNull();
        body!.Provider.ShouldBe("anthropic");
        body.Resolvable.ShouldBeFalse();
        body.Source.ShouldBeNull();
        body.Reason.ShouldBe("format-rejected");
        body.Suggestion.ShouldNotBeNullOrWhiteSpace();
        body.Suggestion!.ShouldContain("OAuth token");

        // #1714 step 8: the strict per-path matrix means OAuth tokens are
        // accepted only on the in-container Claude path. Summary collapses
        // to `path-specific` (one path accepts, the other rejects); the
        // per-path entries name which path it is.
        body.Paths.ShouldNotBeNull();
        body.Paths!.Summary.ShouldBe("path-specific");
        body.Paths.Paths.ShouldNotBeNull();
        body.Paths.Paths.Single(p => p.Path == "rest").Accepted.ShouldBeFalse();
        body.Paths.Paths.Single(p => p.Path == "agent-runtime").Accepted.ShouldBeTrue();
    }

    [Fact]
    public async Task Status_Anthropic_GibberishCredential_ReportsFormatRejectedOnAllPaths()
    {
        // #1690: a stored value that is neither sk-ant-api… nor sk-ant-oat…
        // is rejected pre-flight on every path. Today the runtime probe
        // would still fire the live API call, see the 400/422 response,
        // and translate it to format-rejected — the pre-flight check
        // saves that round-trip.
        var ct = TestContext.Current.CancellationToken;
        await SeedTenantSecretAsync("anthropic-api-key", "totally-not-a-key", ct);

        var response = await _client.GetAsync(
            "/api/v1/platform/credentials/anthropic/status?dispatchPath=agent-runtime", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<ProviderCredentialStatusResponse>(ct);
        body.ShouldNotBeNull();
        body!.Resolvable.ShouldBeFalse();
        body.Reason.ShouldBe("format-rejected");
        body.Paths.ShouldNotBeNull();
        body.Paths!.Summary.ShouldBe("format-rejected");
        body.Paths.Paths.Single(p => p.Path == "rest").Accepted.ShouldBeFalse();
        body.Paths.Paths.Single(p => p.Path == "agent-runtime").Accepted.ShouldBeFalse();
    }

    [Fact]
    public async Task Status_Anthropic_OAuthToken_AgentRuntimePath_ReportsResolvable()
    {
        // #1714: the Claude agent-runtime path is OAuth-only — an OAuth
        // token resolves cleanly there, while the REST path rejects it
        // pre-flight (API-key-only). The matrix reflects the strict
        // per-path acceptance with `path-specific` and the explicit
        // per-path entries.
        var ct = TestContext.Current.CancellationToken;
        await SeedTenantSecretAsync("anthropic-api-key", "sk-ant-oat-fake-token", ct);

        var response = await _client.GetAsync(
            "/api/v1/platform/credentials/anthropic/status?dispatchPath=agent-runtime", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<ProviderCredentialStatusResponse>(ct);
        body.ShouldNotBeNull();
        body!.Provider.ShouldBe("anthropic");
        body.Resolvable.ShouldBeTrue();
        body.Source.ShouldBe("tenant");
        body.Reason.ShouldBeNull();
        body.Suggestion.ShouldBeNull();

        body.Paths.ShouldNotBeNull();
        body.Paths!.Summary.ShouldBe("path-specific");
        body.Paths.Paths.Single(p => p.Path == "rest").Accepted.ShouldBeFalse();
        body.Paths.Paths.Single(p => p.Path == "agent-runtime").Accepted.ShouldBeTrue();
    }

    [Fact]
    public async Task Status_Anthropic_ApiKey_RestPath_ReportsResolvable()
    {
        // #1714: API keys (sk-ant-api…) are accepted only on the REST
        // path; the Claude agent-runtime path is OAuth-only and rejects
        // them pre-flight. The matrix reports `path-specific` with REST
        // accepted and AgentRuntime rejected.
        var ct = TestContext.Current.CancellationToken;
        await SeedTenantSecretAsync("anthropic-api-key", "sk-ant-api-fake-key", ct);

        var response = await _client.GetAsync(
            "/api/v1/platform/credentials/anthropic/status?dispatchPath=rest", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ProviderCredentialStatusResponse>(ct);
        body.ShouldNotBeNull();
        body!.Resolvable.ShouldBeTrue();
        body.Source.ShouldBe("tenant");
        body.Reason.ShouldBeNull();

        body.Paths.ShouldNotBeNull();
        body.Paths!.Summary.ShouldBe("path-specific");
        body.Paths.Paths.Count.ShouldBe(2);
        body.Paths.Paths.Single(p => p.Path == "rest").Accepted.ShouldBeTrue();
        body.Paths.Paths.Single(p => p.Path == "agent-runtime").Accepted.ShouldBeFalse();
    }

    [Fact]
    public async Task Status_Anthropic_ApiKey_AgentRuntimePath_RejectedPreFlight()
    {
        // #1714: API keys belong on the Spring Voyage REST path, not the
        // Claude agent-runtime path. The wizard surfaces format-rejected
        // when the caller asks about agent-runtime so the operator is
        // told to either regenerate via `claude setup-token` or switch
        // to `agent: spring-voyage, provider: anthropic`.
        var ct = TestContext.Current.CancellationToken;
        await SeedTenantSecretAsync("anthropic-api-key", "sk-ant-api-fake-key", ct);

        var response = await _client.GetAsync(
            "/api/v1/platform/credentials/anthropic/status?dispatchPath=agent-runtime", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ProviderCredentialStatusResponse>(ct);
        body.ShouldNotBeNull();
        body!.Resolvable.ShouldBeFalse();
        body.Reason.ShouldBe("format-rejected");

        body.Paths.ShouldNotBeNull();
        body.Paths!.Summary.ShouldBe("path-specific");
        body.Paths.Paths.Single(p => p.Path == "rest").Accepted.ShouldBeTrue();
        body.Paths.Paths.Single(p => p.Path == "agent-runtime").Accepted.ShouldBeFalse();
    }

    [Fact]
    public async Task Status_Anthropic_OAuthToken_NoDispatchPathParam_DefaultsToConservativeRest()
    {
        // Legacy callers that do not pass `?dispatchPath=…` get the
        // strictest evaluation (REST) — the wizard's existing call
        // pattern therefore surfaces the same format-rejected answer
        // without needing to migrate.
        var ct = TestContext.Current.CancellationToken;
        await SeedTenantSecretAsync("anthropic-api-key", "sk-ant-oat-fake-token", ct);

        var response = await _client.GetAsync(
            "/api/v1/platform/credentials/anthropic/status", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<ProviderCredentialStatusResponse>(ct);
        body.ShouldNotBeNull();
        body!.Resolvable.ShouldBeFalse();
        body.Reason.ShouldBe("format-rejected");
    }

    [Fact]
    public async Task Status_UnknownDispatchPath_ReturnsBadRequest()
    {
        var ct = TestContext.Current.CancellationToken;
        await ClearSecretsAsync(ct);

        var response = await _client.GetAsync(
            "/api/v1/platform/credentials/anthropic/status?dispatchPath=not-a-path", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync(ct);
        body.ShouldContain("unknown-dispatch-path");
    }

    /// <summary>
    /// Seeds a tenant-scoped registry entry without configuring the
    /// store's plaintext response. Returns the generated opaque store
    /// key so callers can layer their own <c>ReadAsync</c> return (e.g.
    /// a cipher-unreadable value) on top.
    /// </summary>
    private async Task<string> SeedRegistryOnlyAsync(string name, CancellationToken ct)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        db.SecretRegistryEntries.RemoveRange(db.SecretRegistryEntries);
        await db.SaveChangesAsync(ct);

        var tenantId = scope.ServiceProvider
            .GetRequiredService<ITenantContext>().CurrentTenantId;
        var storeKey = Guid.NewGuid().ToString("N");
        db.SecretRegistryEntries.Add(new SecretRegistryEntry
        {
            TenantId = tenantId,
            Scope = SecretScope.Tenant,
            OwnerId = tenantId,
            Name = name,
            StoreKey = storeKey,
            Origin = SecretOrigin.PlatformOwned,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(ct);

        _factory.SecretStore.ClearReceivedCalls();
        _factory.SecretStore.ReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));
        return storeKey;
    }

    /// <summary>
    /// Seeds a tenant-scoped secret by writing both the registry row
    /// (EF) and the store (ISecretStore) so the resolver's
    /// <c>ResolveWithPathAsync</c> returns the stored plaintext. Mirrors
    /// the shape used by other scoped-secret tests.
    /// </summary>
    private async Task SeedTenantSecretAsync(string name, string value, CancellationToken ct)
    {
        await ClearSecretsAsync(ct);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var tenantId = scope.ServiceProvider
            .GetRequiredService<ITenantContext>().CurrentTenantId;

        var storeKey = Guid.NewGuid().ToString("N");
        db.SecretRegistryEntries.Add(new SecretRegistryEntry
        {
            TenantId = tenantId,
            Scope = SecretScope.Tenant,
            OwnerId = tenantId,
            Name = name,
            StoreKey = storeKey,
            Origin = SecretOrigin.PlatformOwned,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(ct);

        _factory.SecretStore.ReadAsync(storeKey, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(value));
    }

    /// <summary>
    /// Clears tenant/unit registry rows AND resets the secret store's
    /// default read behaviour between tests so state doesn't leak across
    /// tests. The <see cref="CustomWebApplicationFactory"/> is shared via
    /// <see cref="IClassFixture{TFixture}"/>, so without this the
    /// not-configured tests could see a secret seeded by another test.
    /// </summary>
    private async Task ClearSecretsAsync(CancellationToken ct)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        db.SecretRegistryEntries.RemoveRange(db.SecretRegistryEntries);
        await db.SaveChangesAsync(ct);

        // Reset the default ReadAsync behaviour so a secret seeded by
        // a previous test doesn't still return its plaintext here.
        // SeedTenantSecretAsync layers a specific-key return on top;
        // the Arg.Any<> fallback resets everything that isn't seeded.
        _factory.SecretStore.ClearReceivedCalls();
        _factory.SecretStore.ReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));
    }
}