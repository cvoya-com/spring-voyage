// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Slack.Tests;

using Cvoya.Spring.Connector.Slack.Configuration;
using Cvoya.Spring.Core.Secrets;
using Cvoya.Spring.Core.Tenancy;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Issue #2849 — asserts <see cref="SlackOAuthOptionsResolver"/>
/// implements the documented precedence chain per credential field:
/// tenant-scoped secret wins over platform-scoped secret wins over
/// env-config field. Non-credential fields (<c>Scopes</c>,
/// <c>StateTtl</c>) always come from env-config.
/// </summary>
public class SlackOAuthOptionsResolverTests
{
    private static readonly Guid TestTenantId = new("aaaaaaaa-bbbb-cccc-dddd-000000000001");

    [Fact]
    public async Task ResolveAsync_TenantScopeBeatsPlatformAndEnv()
    {
        var ct = TestContext.Current.CancellationToken;
        var resolver = BuildResolver(
            tenantValues: new()
            {
                [SlackOAuthOptionsResolver.SecretNames.ClientId] = "tenant-client-id",
                [SlackOAuthOptionsResolver.SecretNames.ClientSecret] = "tenant-client-secret",
                [SlackOAuthOptionsResolver.SecretNames.SigningSecret] = "tenant-signing-secret",
                [SlackOAuthOptionsResolver.SecretNames.RedirectUri] = "https://tenant.example/callback",
            },
            platformValues: new()
            {
                [SlackOAuthOptionsResolver.SecretNames.ClientId] = "platform-client-id",
                [SlackOAuthOptionsResolver.SecretNames.ClientSecret] = "platform-client-secret",
                [SlackOAuthOptionsResolver.SecretNames.SigningSecret] = "platform-signing-secret",
                [SlackOAuthOptionsResolver.SecretNames.RedirectUri] = "https://platform.example/callback",
            },
            envOptions: new SlackOAuthOptions
            {
                ClientId = "env-client-id",
                ClientSecret = "env-client-secret",
                SigningSecret = "env-signing-secret",
                RedirectUri = "https://env.example/callback",
                Scopes = "env:scope",
                StateTtl = TimeSpan.FromMinutes(7),
            });

        var resolved = await resolver.ResolveAsync(ct);

        resolved.ClientId.ShouldBe("tenant-client-id");
        resolved.ClientSecret.ShouldBe("tenant-client-secret");
        resolved.SigningSecret.ShouldBe("tenant-signing-secret");
        resolved.RedirectUri.ShouldBe("https://tenant.example/callback");
        // Non-credential fields always come from env-config.
        resolved.Scopes.ShouldBe("env:scope");
        resolved.StateTtl.ShouldBe(TimeSpan.FromMinutes(7));
    }

    [Fact]
    public async Task ResolveAsync_PlatformScopeBeatsEnvWhenTenantMisses()
    {
        var ct = TestContext.Current.CancellationToken;
        var resolver = BuildResolver(
            tenantValues: new(),
            platformValues: new()
            {
                [SlackOAuthOptionsResolver.SecretNames.ClientId] = "platform-client-id",
                [SlackOAuthOptionsResolver.SecretNames.ClientSecret] = "platform-client-secret",
                [SlackOAuthOptionsResolver.SecretNames.SigningSecret] = "platform-signing-secret",
                [SlackOAuthOptionsResolver.SecretNames.RedirectUri] = "https://platform.example/callback",
            },
            envOptions: new SlackOAuthOptions
            {
                ClientId = "env-client-id",
                ClientSecret = "env-client-secret",
                SigningSecret = "env-signing-secret",
                RedirectUri = "https://env.example/callback",
            });

        var resolved = await resolver.ResolveAsync(ct);

        resolved.ClientId.ShouldBe("platform-client-id");
        resolved.ClientSecret.ShouldBe("platform-client-secret");
        resolved.SigningSecret.ShouldBe("platform-signing-secret");
        resolved.RedirectUri.ShouldBe("https://platform.example/callback");
    }

    [Fact]
    public async Task ResolveAsync_EnvFallbackWhenNeitherSecretScopeHasValue()
    {
        var ct = TestContext.Current.CancellationToken;
        var resolver = BuildResolver(
            tenantValues: new(),
            platformValues: new(),
            envOptions: new SlackOAuthOptions
            {
                ClientId = "env-client-id",
                ClientSecret = "env-client-secret",
                SigningSecret = "env-signing-secret",
                RedirectUri = "https://env.example/callback",
            });

        var resolved = await resolver.ResolveAsync(ct);

        resolved.ClientId.ShouldBe("env-client-id");
        resolved.ClientSecret.ShouldBe("env-client-secret");
        resolved.SigningSecret.ShouldBe("env-signing-secret");
        resolved.RedirectUri.ShouldBe("https://env.example/callback");
    }

    // ---- Harness ----

    private static SlackOAuthOptionsResolver BuildResolver(
        Dictionary<string, string> tenantValues,
        Dictionary<string, string> platformValues,
        SlackOAuthOptions envOptions)
    {
        var services = new ServiceCollection();

        var tenantContext = Substitute.For<ITenantContext>();
        tenantContext.CurrentTenantId.Returns(TestTenantId);
        services.AddSingleton(tenantContext);

        var secretResolver = Substitute.For<ISecretResolver>();
        secretResolver
            .ResolveAsync(Arg.Any<SecretRef>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var @ref = call.Arg<SecretRef>();
                if (@ref.Scope == SecretScope.Tenant
                    && @ref.OwnerId == TestTenantId
                    && tenantValues.TryGetValue(@ref.Name, out var tenantValue))
                {
                    return Task.FromResult<string?>(tenantValue);
                }
                if (@ref.Scope == SecretScope.Platform
                    && @ref.OwnerId is null
                    && platformValues.TryGetValue(@ref.Name, out var platformValue))
                {
                    return Task.FromResult<string?>(platformValue);
                }
                return Task.FromResult<string?>(null);
            });
        services.AddSingleton(secretResolver);

        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var envMonitor = Substitute.For<IOptionsMonitor<SlackOAuthOptions>>();
        envMonitor.CurrentValue.Returns(envOptions);

        return new SlackOAuthOptionsResolver(
            scopeFactory,
            envMonitor,
            NullLoggerFactory.Instance);
    }
}
