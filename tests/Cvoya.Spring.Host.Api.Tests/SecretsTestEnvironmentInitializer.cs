// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests;

using System;
using System.Runtime.CompilerServices;

/// <summary>
/// Sets <c>SPRING_SECRETS_AES_KEY</c> for the duration of every test in
/// this assembly. Required because the platform no longer ships an
/// in-memory ephemeral dev key — every <c>SecretsEncryptor</c> instance
/// constructed during host startup needs a real base64 32-byte key. We
/// set it once at module load (before any test factory builds a host)
/// rather than on every <c>UseSetting</c> call site, which is what
/// <c>Cvoya.Spring.Dapr.Tests</c>'s collection-based serialiser does.
/// The value is a freshly-generated random 32-byte base64 string,
/// non-weak by the classifier's standards, and irrelevant outside the
/// test process.
/// </summary>
internal static class SecretsTestEnvironmentInitializer
{
    /// <summary>Deterministic AES-256 base64 key for tests.</summary>
    public const string TestAesKeyBase64 = "8w7eyN4Jf1g3AX9BPkej9gV2hWV1LO6lWIvs6RRQcAw=";

    [ModuleInitializer]
    public static void Initialize()
    {
        // Only set when not already configured — keeps an operator's
        // ambient SPRING_SECRETS_AES_KEY (e.g. when running tests against
        // a key they care about) from being silently overridden.
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SPRING_SECRETS_AES_KEY")))
        {
            Environment.SetEnvironmentVariable("SPRING_SECRETS_AES_KEY", TestAesKeyBase64);
        }
    }
}
