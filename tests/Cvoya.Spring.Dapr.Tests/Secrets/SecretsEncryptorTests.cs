// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Secrets;

using System;
using System.IO;
using System.Security.Cryptography;

using Cvoya.Spring.Core.Secrets;
using Cvoya.Spring.Dapr.Secrets;
using Cvoya.Spring.Dapr.Tenancy;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for <see cref="SecretsEncryptor"/>: envelope round-trip, AAD
/// binding, legacy fallback, key-source precedence, and the weak-key
/// self-check at startup.
/// </summary>
[Collection(SecretsEnvironmentVariableCollection.Name)]
public class SecretsEncryptorTests : IDisposable
{
    private const string EnvVar = SecretsEncryptor.KeyEnvironmentVariable;
    private readonly string? _savedEnv = Environment.GetEnvironmentVariable(EnvVar);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(EnvVar, _savedEnv);
        GC.SuppressFinalize(this);
    }

    private static ILogger<SecretsEncryptor> Log() => Substitute.For<ILogger<SecretsEncryptor>>();

    private static IOptions<SecretsOptions> Opts(
        bool allowEphemeralDevKey = false,
        string? aesKeyFile = null) =>
        Options.Create(new SecretsOptions
        {
            AllowEphemeralDevKey = allowEphemeralDevKey,
            AesKeyFile = aesKeyFile,
        });

    [Fact]
    public void Ctor_NoKeyConfigured_Throws()
    {
        Environment.SetEnvironmentVariable(EnvVar, null);

        var ex = Should.Throw<InvalidOperationException>(() => new SecretsEncryptor(Opts(), Log()));
        ex.Message.ShouldContain(EnvVar);
        ex.Message.ShouldContain("AesKeyFile");
        ex.Message.ShouldContain("AllowEphemeralDevKey");
    }

    [Fact]
    public void Ctor_EphemeralDevKey_Allowed()
    {
        Environment.SetEnvironmentVariable(EnvVar, null);
        var sut = new SecretsEncryptor(Opts(allowEphemeralDevKey: true), Log());

        var envelope = sut.Encrypt("hunter2", "acme", "k1");
        var round = sut.Decrypt(envelope, "acme", "k1", out var wasEnveloped);

        wasEnveloped.ShouldBeTrue();
        round.ShouldBe("hunter2");
    }

    [Fact]
    public void Ctor_EnvKeyAllZeros_ThrowsWeakKey()
    {
        var key = Convert.ToBase64String(new byte[32]);
        Environment.SetEnvironmentVariable(EnvVar, key);

        var ex = Should.Throw<InvalidOperationException>(() => new SecretsEncryptor(Opts(), Log()));
        ex.Message.ShouldContain("all zeros");
    }

    [Fact]
    public void Ctor_EnvKeyAllOnes_ThrowsWeakKey()
    {
        var bytes = new byte[32];
        Array.Fill(bytes, (byte)0xFF);
        Environment.SetEnvironmentVariable(EnvVar, Convert.ToBase64String(bytes));

        var ex = Should.Throw<InvalidOperationException>(() => new SecretsEncryptor(Opts(), Log()));
        ex.Message.ShouldContain("0xFF");
    }

    [Fact]
    public void Ctor_EnvKeyAscending_ThrowsSentinel()
    {
        var bytes = new byte[32];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)i;
        }
        Environment.SetEnvironmentVariable(EnvVar, Convert.ToBase64String(bytes));

        var ex = Should.Throw<InvalidOperationException>(() => new SecretsEncryptor(Opts(), Log()));
        ex.Message.ShouldContain("sentinel");
    }

    [Fact]
    public void Ctor_EnvKeyWrongLength_Throws()
    {
        Environment.SetEnvironmentVariable(EnvVar, Convert.ToBase64String(new byte[16]));

        var ex = Should.Throw<InvalidOperationException>(() => new SecretsEncryptor(Opts(), Log()));
        ex.Message.ShouldContain("32 bytes");
    }

    [Fact]
    public void Ctor_EnvKeyInvalidBase64_Throws()
    {
        Environment.SetEnvironmentVariable(EnvVar, "not-base64!!!");

        var ex = Should.Throw<InvalidOperationException>(() => new SecretsEncryptor(Opts(), Log()));
        ex.Message.ShouldContain("base64");
    }

    [Fact]
    public void Ctor_EnvKey_TakesPriorityOverFile()
    {
        var envBytes = RandomNumberGenerator.GetBytes(32);
        var fileBytes = RandomNumberGenerator.GetBytes(32);

        var path = Path.Combine(Path.GetTempPath(), "spring-secrets-key-" + Guid.NewGuid().ToString("N") + ".key");
        File.WriteAllText(path, Convert.ToBase64String(fileBytes));
        try
        {
            Environment.SetEnvironmentVariable(EnvVar, Convert.ToBase64String(envBytes));
            var sut = new SecretsEncryptor(Opts(aesKeyFile: path), Log());

            // Envelope encrypted with env key should round-trip.
            var envelope = sut.Encrypt("hunter2", "acme", "k1");
            sut.Decrypt(envelope, "acme", "k1", out _).ShouldBe("hunter2");

            // But a new encryptor built from just the file key should NOT
            // decrypt the env-key envelope.
            Environment.SetEnvironmentVariable(EnvVar, null);
            var fileSut = new SecretsEncryptor(Opts(aesKeyFile: path), Log());
            Should.Throw<SecretUnreadableException>(
                () => fileSut.Decrypt(envelope, "acme", "k1", out _));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Ctor_AesKeyFile_MissingFile_Throws()
    {
        Environment.SetEnvironmentVariable(EnvVar, null);
        var path = Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N") + ".key");

        var ex = Should.Throw<InvalidOperationException>(() =>
            new SecretsEncryptor(Opts(aesKeyFile: path), Log()));
        ex.Message.ShouldContain(path);
    }

    [Fact]
    public void EncryptDecrypt_RoundTrip()
    {
        var sut = CreateWithFreshKey();
        var envelope = sut.Encrypt("hunter2", "acme", "k1");
        sut.Decrypt(envelope, "acme", "k1", out var wasEnveloped).ShouldBe("hunter2");
        wasEnveloped.ShouldBeTrue();
    }

    [Fact]
    public void Encrypt_UsesFreshNonceEachTime()
    {
        var sut = CreateWithFreshKey();
        var a = sut.Encrypt("hunter2", "acme", "k1");
        var b = sut.Encrypt("hunter2", "acme", "k1");
        a.ShouldNotBe(b);
    }

    [Fact]
    public void Decrypt_WrongTenant_ThrowsSecretUnreadable()
    {
        var sut = CreateWithFreshKey();
        var envelope = sut.Encrypt("hunter2", "acme", "k1");

        var ex = Should.Throw<SecretUnreadableException>(
            () => sut.Decrypt(envelope, "mallory", "k1", out _));
        ex.InnerException.ShouldBeAssignableTo<CryptographicException>();
    }

    [Fact]
    public void Decrypt_WrongStoreKey_ThrowsSecretUnreadable()
    {
        var sut = CreateWithFreshKey();
        var envelope = sut.Encrypt("hunter2", "acme", "k1");

        var ex = Should.Throw<SecretUnreadableException>(
            () => sut.Decrypt(envelope, "acme", "k2", out _));
        ex.InnerException.ShouldBeAssignableTo<CryptographicException>();
    }

    [Fact]
    public void Decrypt_LegacyPlaintext_ReturnedVerbatim()
    {
        var sut = CreateWithFreshKey();
        var result = sut.Decrypt("plain-legacy-value", "acme", "k1", out var wasEnveloped);
        result.ShouldBe("plain-legacy-value");
        wasEnveloped.ShouldBeFalse();
    }

    [Fact]
    public void Decrypt_ValidBase64ButNotEnvelope_TreatedAsLegacy()
    {
        var sut = CreateWithFreshKey();
        // Valid base64, but no version-1 prefix — falls back to legacy.
        var looksLikeBase64 = Convert.ToBase64String(new byte[] { 0x99, 0x00, 0x00, 0x00 });
        var result = sut.Decrypt(looksLikeBase64, "acme", "k1", out var wasEnveloped);
        result.ShouldBe(looksLikeBase64);
        wasEnveloped.ShouldBeFalse();
    }

    [Fact]
    public void PlatformScope_UsesLiteralPlatformAsTenantId()
    {
        // Sanity check: the caller chooses the tenantId string. "platform"
        // is the idiomatic value for platform-scoped secrets and round-
        // trips like any other.
        var sut = CreateWithFreshKey();
        var envelope = sut.Encrypt("token", "platform", "k1");
        sut.Decrypt(envelope, "platform", "k1", out _).ShouldBe("token");
        Should.Throw<SecretUnreadableException>(
            () => sut.Decrypt(envelope, "some-tenant", "k1", out _));
    }

    private SecretsEncryptor CreateWithFreshKey()
    {
        Environment.SetEnvironmentVariable(EnvVar, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        return new SecretsEncryptor(Opts(), Log());
    }
}