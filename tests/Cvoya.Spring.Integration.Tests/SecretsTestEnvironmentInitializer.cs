// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Integration.Tests;

using System;
using System.Runtime.CompilerServices;

/// <summary>
/// Sets <c>SPRING_SECRETS_AES_KEY</c> for the duration of every test in
/// this assembly. Required because the platform no longer ships an
/// in-memory ephemeral dev key — every <c>SecretsEncryptor</c> instance
/// constructed during integration-host startup needs a real base64 32-byte key.
/// </summary>
internal static class SecretsTestEnvironmentInitializer
{
    /// <summary>Deterministic AES-256 base64 key for tests.</summary>
    public const string TestAesKeyBase64 = "8w7eyN4Jf1g3AX9BPkej9gV2hWV1LO6lWIvs6RRQcAw=";

    [ModuleInitializer]
    public static void Initialize()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SPRING_SECRETS_AES_KEY")))
        {
            Environment.SetEnvironmentVariable("SPRING_SECRETS_AES_KEY", TestAesKeyBase64);
        }
    }
}
