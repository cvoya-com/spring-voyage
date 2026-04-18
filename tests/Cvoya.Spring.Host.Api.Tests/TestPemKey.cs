// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests;

using System.Security.Cryptography;

/// <summary>
/// Generates a valid PEM-encoded RSA private key for tests that need one
/// to satisfy the GitHub connector's connector-init credential validator
/// (#609). Cached per test run; the value is generated in-memory and never
/// checked in.
/// </summary>
internal static class TestPemKey
{
    private static readonly Lazy<string> Instance = new(Generate);

    /// <summary>
    /// A cached valid RSA PEM private key shared across tests in this assembly.
    /// </summary>
    public static string Value => Instance.Value;

    private static string Generate()
    {
        using var rsa = RSA.Create(2048);
        return rsa.ExportRSAPrivateKeyPem();
    }
}