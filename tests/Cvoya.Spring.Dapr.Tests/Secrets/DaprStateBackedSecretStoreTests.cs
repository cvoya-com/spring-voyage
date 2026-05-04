// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Secrets;

using System.Collections.Generic;

using Cvoya.Spring.Core.Secrets;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Secrets;
using Cvoya.Spring.Dapr.Tenancy;

using global::Dapr.Client;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for <see cref="DaprStateBackedSecretStore"/>. Verifies that the
/// returned storeKey is an opaque GUID, that plaintext round-trips
/// through the <see cref="DaprClient"/> with AES-GCM envelope encryption,
/// that tenant-bound AAD is enforced, and that the per-tenant component
/// format path (<see cref="SecretsOptions.ComponentNameFormat"/>) resolves
/// the correct component.
/// </summary>
public class DaprStateBackedSecretStoreTests
{
    private const string Component = "statestore";
    private static readonly Guid TenantId = new("aaaaaaaa-1111-1111-1111-000000000001");

    private readonly DaprClient _dapr = Substitute.For<DaprClient>();

    private static ITenantContext TenantContext(Guid? tenantId = null)
    {
        var ctx = Substitute.For<ITenantContext>();
        ctx.CurrentTenantId.Returns(tenantId ?? TenantId);
        return ctx;
    }

    private static ISecretsEncryptor RealEncryptor(bool allowEphemeral = true)
    {
        var options = Options.Create(new SecretsOptions
        {
            AllowEphemeralDevKey = allowEphemeral,
        });
        var logger = Substitute.For<ILogger<SecretsEncryptor>>();
        return new SecretsEncryptor(options, logger);
    }

    private DaprStateBackedSecretStore CreateSut(
        SecretsOptions? options = null,
        ISecretsEncryptor? encryptor = null,
        ITenantContext? tenantContext = null)
    {
        var opts = Options.Create(options ?? new SecretsOptions
        {
            StoreComponent = Component,
            KeyPrefix = "secrets/",
        });
        var logger = Substitute.For<ILogger<DaprStateBackedSecretStore>>();
        return new DaprStateBackedSecretStore(
            _dapr,
            encryptor ?? RealEncryptor(),
            tenantContext ?? TenantContext(),
            opts,
            logger);
    }

    [Fact]
    public async Task WriteAsync_ReturnsOpaqueGuidStoreKey()
    {
        var ct = TestContext.Current.CancellationToken;

        var sut = CreateSut();
        var key = await sut.WriteAsync("hunter2", ct);

        key.ShouldNotBeNullOrWhiteSpace();
        Guid.TryParseExact(key, "N", out _).ShouldBeTrue();
        key.ShouldNotContain("hunter2");
    }

    [Fact]
    public async Task WriteAsync_EncryptsPlaintextBeforeHandingToDapr()
    {
        var ct = TestContext.Current.CancellationToken;

        var sut = CreateSut();
        var key = await sut.WriteAsync("hunter2", ct);

        await _dapr.Received(1).SaveStateAsync(
            Component,
            $"secrets/{key}",
            Arg.Is<string>(v => !v.Contains("hunter2")),
            Arg.Any<StateOptions?>(),
            Arg.Any<IReadOnlyDictionary<string, string>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WriteAsync_BackendKey_DoesNotEncodeTenant()
    {
        var ct = TestContext.Current.CancellationToken;

        var sut = CreateSut();
        await sut.WriteAsync("hunter2", ct);

        await _dapr.Received().SaveStateAsync(
            Component,
            Arg.Is<string>(k => !k.Contains("acme") && !k.Contains("local") && !k.Contains("tenant")),
            Arg.Any<string>(),
            Arg.Any<StateOptions?>(),
            Arg.Any<IReadOnlyDictionary<string, string>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReadAsync_RoundTripsThroughEncryptor()
    {
        var ct = TestContext.Current.CancellationToken;
        var encryptor = RealEncryptor();
        var sut = CreateSut(encryptor: encryptor);

        string? captured = null;
        _dapr
            .When(d => d.SaveStateAsync(
                Component,
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<StateOptions?>(),
                Arg.Any<IReadOnlyDictionary<string, string>?>(),
                Arg.Any<CancellationToken>()))
            .Do(ci => captured = ci.ArgAt<string>(2));

        var storeKey = await sut.WriteAsync("hunter2", ct);

        _dapr
            .GetStateAsync<string?>(Component, $"secrets/{storeKey}", cancellationToken: Arg.Any<CancellationToken>())
            .Returns(captured);

        var round = await sut.ReadAsync(storeKey, ct);
        round.ShouldBe("hunter2");
    }

    [Fact]
    public async Task ReadAsync_LegacyPlaintextValue_ReturnedAsIs()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = CreateSut();

        _dapr
            .GetStateAsync<string?>(Component, "secrets/abc", cancellationToken: Arg.Any<CancellationToken>())
            .Returns("legacy-plaintext");

        var result = await sut.ReadAsync("abc", ct);
        result.ShouldBe("legacy-plaintext");
    }

    [Fact]
    public async Task ReadAsync_EnvelopedWithWrongTenant_FailsAuthentication()
    {
        var ct = TestContext.Current.CancellationToken;
        var encryptor = RealEncryptor();

        // Encrypt as tenant Acme but attempt to read with the store
        // configured for tenant Mallory — the AAD mismatch must fail.
        var acme = new Guid("aaaaaaaa-1111-1111-1111-000000000001");
        var mallory = new Guid("aaaaaaaa-1111-1111-1111-000000000099");
        var envelope = encryptor.Encrypt("hunter2", acme, "storekey123");

        var sut = CreateSut(encryptor: encryptor, tenantContext: TenantContext(mallory));

        _dapr
            .GetStateAsync<string?>(Component, "secrets/storekey123", cancellationToken: Arg.Any<CancellationToken>())
            .Returns(envelope);

        // The encryptor wraps the underlying CryptographicException in
        // a domain SecretUnreadableException so upstream callers (e.g.
        // the credential-status endpoint) can distinguish "unreadable"
        // from other infrastructure errors.
        await Should.ThrowAsync<SecretUnreadableException>(
            async () => await sut.ReadAsync("storekey123", ct));
    }

    [Fact]
    public async Task DeleteAsync_CallsDaprClient()
    {
        var ct = TestContext.Current.CancellationToken;

        var sut = CreateSut();
        await sut.DeleteAsync("abc", ct);

        await _dapr.Received(1).DeleteStateAsync(
            Component,
            "secrets/abc",
            Arg.Any<StateOptions?>(),
            Arg.Any<IReadOnlyDictionary<string, string>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WriteAsync_PerTenantComponentFormat_ResolvesPerTenantComponent()
    {
        var ct = TestContext.Current.CancellationToken;

        var sut = CreateSut(options: new SecretsOptions
        {
            StoreComponent = "statestore",
            ComponentNameFormat = "statestore-{tenantId}",
            KeyPrefix = "secrets/",
        }, tenantContext: TenantContext());

        await sut.WriteAsync("hunter2", ct);

        var expectedComponent = $"statestore-{TenantId:N}";
        await _dapr.Received(1).SaveStateAsync(
            expectedComponent,
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<StateOptions?>(),
            Arg.Any<IReadOnlyDictionary<string, string>?>(),
            Arg.Any<CancellationToken>());
        await _dapr.DidNotReceive().SaveStateAsync(
            "statestore",
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<StateOptions?>(),
            Arg.Any<IReadOnlyDictionary<string, string>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReadAsync_PerTenantComponentFormat_UsesPerTenantComponent()
    {
        var ct = TestContext.Current.CancellationToken;

        var sut = CreateSut(options: new SecretsOptions
        {
            ComponentNameFormat = "statestore-{tenantId}",
            KeyPrefix = "secrets/",
        }, tenantContext: TenantContext());

        var expectedComponent = $"statestore-{TenantId:N}";
        _dapr
            .GetStateAsync<string?>(expectedComponent, "secrets/abc", cancellationToken: Arg.Any<CancellationToken>())
            .Returns("legacy-plaintext");

        var result = await sut.ReadAsync("abc", ct);
        result.ShouldBe("legacy-plaintext");
    }

    [Fact]
    public async Task ReadAsync_LegacyTenantPrefixedKey_Fallback()
    {
        var ct = TestContext.Current.CancellationToken;

        var sut = CreateSut(tenantContext: TenantContext());

        // Canonical key misses.
        _dapr
            .GetStateAsync<string?>(Component, "secrets/abc", cancellationToken: Arg.Any<CancellationToken>())
            .Returns((string?)null);
        // Legacy key hits with a plain value (legacy form: secrets/<tenantHex>/<key>).
        _dapr
            .GetStateAsync<string?>(Component, $"secrets/{TenantId:N}/abc", cancellationToken: Arg.Any<CancellationToken>())
            .Returns("hunter2");

        var result = await sut.ReadAsync("abc", ct);
        result.ShouldBe("hunter2");
    }
}