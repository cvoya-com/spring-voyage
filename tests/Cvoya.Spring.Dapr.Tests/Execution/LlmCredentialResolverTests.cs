// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Execution;

using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Secrets;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Execution;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for <see cref="LlmCredentialResolver"/> — the tier-2 credential
/// resolver introduced by #615. Verifies the unit → tenant resolution
/// order, the correct provider-to-secret-name mapping, and the
/// fail-clean behaviour when nothing is configured.
/// </summary>
public class LlmCredentialResolverTests
{
    private const string TenantId = "acme";

    private static LlmCredentialResolver CreateSut(ISecretResolver resolver)
    {
        var tenantContext = Substitute.For<ITenantContext>();
        tenantContext.CurrentTenantId.Returns(TenantId);
        return new LlmCredentialResolver(resolver, tenantContext, NullLogger<LlmCredentialResolver>.Instance);
    }

    [Fact]
    public async Task ResolveAsync_UnknownProvider_ReturnsNotFound()
    {
        var resolver = Substitute.For<ISecretResolver>();
        var sut = CreateSut(resolver);

        var result = await sut.ResolveAsync("no-such-provider", unitName: null, TestContext.Current.CancellationToken);

        result.Value.ShouldBeNull();
        result.Source.ShouldBe(LlmCredentialSource.NotFound);
        result.SecretName.ShouldBeEmpty();
        await resolver.DidNotReceiveWithAnyArgs().ResolveWithPathAsync(
            default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ResolveAsync_UnitScopedHit_ReturnsUnitSource()
    {
        var ct = TestContext.Current.CancellationToken;
        var resolver = Substitute.For<ISecretResolver>();
        resolver.ResolveWithPathAsync(
                Arg.Is<SecretRef>(r => r.Scope == SecretScope.Unit && r.OwnerId == "u1" && r.Name == "anthropic-api-key"),
                ct)
            .Returns(new SecretResolution("sk-unit", SecretResolvePath.Direct, new SecretRef(SecretScope.Unit, "u1", "anthropic-api-key")));
        var sut = CreateSut(resolver);

        var result = await sut.ResolveAsync("claude", unitName: "u1", ct);

        result.Value.ShouldBe("sk-unit");
        result.Source.ShouldBe(LlmCredentialSource.Unit);
        result.SecretName.ShouldBe("anthropic-api-key");
    }

    [Fact]
    public async Task ResolveAsync_UnitMissesTenantHas_ReportsTenantSource()
    {
        var ct = TestContext.Current.CancellationToken;
        var resolver = Substitute.For<ISecretResolver>();
        var unitRef = new SecretRef(SecretScope.Unit, "u1", "openai-api-key");
        var tenantRef = new SecretRef(SecretScope.Tenant, TenantId, "openai-api-key");
        resolver.ResolveWithPathAsync(unitRef, ct)
            .Returns(new SecretResolution(
                "sk-tenant",
                SecretResolvePath.InheritedFromTenant,
                tenantRef));
        var sut = CreateSut(resolver);

        var result = await sut.ResolveAsync("openai", unitName: "u1", ct);

        result.Value.ShouldBe("sk-tenant");
        result.Source.ShouldBe(LlmCredentialSource.Tenant);
        result.SecretName.ShouldBe("openai-api-key");
    }

    [Fact]
    public async Task ResolveAsync_NoUnit_QueriesTenantDirectly()
    {
        var ct = TestContext.Current.CancellationToken;
        var resolver = Substitute.For<ISecretResolver>();
        resolver.ResolveWithPathAsync(
                Arg.Is<SecretRef>(r => r.Scope == SecretScope.Tenant && r.OwnerId == TenantId && r.Name == "anthropic-api-key"),
                ct)
            .Returns(new SecretResolution(
                "sk-tenant-default",
                SecretResolvePath.Direct,
                new SecretRef(SecretScope.Tenant, TenantId, "anthropic-api-key")));
        var sut = CreateSut(resolver);

        var result = await sut.ResolveAsync("claude", unitName: null, ct);

        result.Value.ShouldBe("sk-tenant-default");
        result.Source.ShouldBe(LlmCredentialSource.Tenant);
        result.SecretName.ShouldBe("anthropic-api-key");
    }

    [Fact]
    public async Task ResolveAsync_UnitAndTenantUnset_ReturnsNotFoundWithSecretName()
    {
        var ct = TestContext.Current.CancellationToken;
        var resolver = Substitute.For<ISecretResolver>();
        resolver.ResolveWithPathAsync(Arg.Any<SecretRef>(), ct)
            .Returns(new SecretResolution(null, SecretResolvePath.NotFound, null));
        var sut = CreateSut(resolver);

        var result = await sut.ResolveAsync("claude", unitName: "u1", ct);

        result.Value.ShouldBeNull();
        result.Source.ShouldBe(LlmCredentialSource.NotFound);
        // Even on NotFound, SecretName is populated so error messages can
        // point operators at the exact secret name they must create.
        result.SecretName.ShouldBe("anthropic-api-key");
    }

    [Fact]
    public async Task ResolveAsync_TenantUnset_NoUnit_ReturnsNotFoundWithSecretName()
    {
        var ct = TestContext.Current.CancellationToken;
        var resolver = Substitute.For<ISecretResolver>();
        resolver.ResolveWithPathAsync(Arg.Any<SecretRef>(), ct)
            .Returns(new SecretResolution(null, SecretResolvePath.NotFound, null));
        var sut = CreateSut(resolver);

        var result = await sut.ResolveAsync("google", unitName: null, ct);

        result.Value.ShouldBeNull();
        result.Source.ShouldBe(LlmCredentialSource.NotFound);
        result.SecretName.ShouldBe("google-api-key");
    }

    [Fact]
    public async Task ResolveAsync_AnthropicAliasFor_Claude()
    {
        var ct = TestContext.Current.CancellationToken;
        var resolver = Substitute.For<ISecretResolver>();
        resolver.ResolveWithPathAsync(Arg.Any<SecretRef>(), ct)
            .Returns(new SecretResolution("sk", SecretResolvePath.Direct, new SecretRef(SecretScope.Tenant, TenantId, "anthropic-api-key")));
        var sut = CreateSut(resolver);

        // Both "claude" and "anthropic" must resolve the same canonical
        // secret name so callers can use either identifier.
        var claude = await sut.ResolveAsync("claude", null, ct);
        var anthropic = await sut.ResolveAsync("anthropic", null, ct);

        claude.SecretName.ShouldBe("anthropic-api-key");
        anthropic.SecretName.ShouldBe("anthropic-api-key");
    }

    [Fact]
    public async Task ResolveAsync_GoogleAliases_AllMapToGoogleApiKey()
    {
        var ct = TestContext.Current.CancellationToken;
        var resolver = Substitute.For<ISecretResolver>();
        resolver.ResolveWithPathAsync(Arg.Any<SecretRef>(), ct)
            .Returns(new SecretResolution("gk", SecretResolvePath.Direct, new SecretRef(SecretScope.Tenant, TenantId, "google-api-key")));
        var sut = CreateSut(resolver);

        var google = await sut.ResolveAsync("google", null, ct);
        var gemini = await sut.ResolveAsync("gemini", null, ct);
        var googleAi = await sut.ResolveAsync("googleai", null, ct);

        google.SecretName.ShouldBe("google-api-key");
        gemini.SecretName.ShouldBe("google-api-key");
        googleAi.SecretName.ShouldBe("google-api-key");
    }
}