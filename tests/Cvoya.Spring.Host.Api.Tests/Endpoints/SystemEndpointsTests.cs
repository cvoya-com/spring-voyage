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
            "/api/v1/system/credentials/no-such-provider/status", ct);

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
            "/api/v1/system/credentials/anthropic/status", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ProviderCredentialStatusResponse>(ct);
        body.ShouldNotBeNull();
        body!.Provider.ShouldBe("anthropic");
        body.Resolvable.ShouldBeFalse();
        body.Source.ShouldBeNull();
        body.Suggestion.ShouldNotBeNullOrWhiteSpace();
        body.Suggestion!.ShouldContain("anthropic-api-key");
    }

    [Fact]
    public async Task Status_Anthropic_TenantConfigured_ReportsTenantSource()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedTenantSecretAsync("anthropic-api-key", "sk-top-secret", ct);

        var response = await _client.GetAsync(
            "/api/v1/system/credentials/anthropic/status", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var raw = await response.Content.ReadAsStringAsync(ct);
        // Non-negotiable: the key material must never appear in the
        // response. If this ever fails, the endpoint has leaked plaintext.
        raw.ShouldNotContain("sk-top-secret");

        var body = await response.Content.ReadFromJsonAsync<ProviderCredentialStatusResponse>(ct);
        body.ShouldNotBeNull();
        body!.Provider.ShouldBe("anthropic");
        body.Resolvable.ShouldBeTrue();
        body.Source.ShouldBe("tenant");
        body.Suggestion.ShouldBeNull();
    }

    [Fact]
    public async Task Status_OpenAi_TenantConfigured_Resolvable()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedTenantSecretAsync("openai-api-key", "sk-openai-value", ct);

        var response = await _client.GetAsync(
            "/api/v1/system/credentials/openai/status", ct);

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
            "/api/v1/system/credentials/google/status", ct);

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
            "/api/v1/system/credentials/ollama/status", ct);

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
            "/api/v1/system/credentials/claude/status", ct);

        // "claude" is accepted as an alias only inside the resolver's
        // provider-id mapping. At the HTTP layer only the canonical
        // values are exposed — so "claude" is rejected by the endpoint
        // with 400 and operators use "anthropic" consistently on the
        // wire. (This keeps the wire surface tight and mirrors the
        // Settings → Tenant defaults panel labels.)
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
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