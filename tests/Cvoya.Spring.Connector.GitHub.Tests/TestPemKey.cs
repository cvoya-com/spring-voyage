// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests;

using System.Security.Cryptography;

/// <summary>
/// Generates a valid PEM-encoded RSA private key for tests that need one
/// to exercise the <c>GitHub:PrivateKeyPem</c> configuration path. The key
/// is regenerated per test run so no secret material gets checked in, and
/// the value parses cleanly through <c>RSA.ImportFromPem</c> so the new
/// connector-init validator in <c>AddCvoyaSpringConnectorGitHub</c> accepts
/// it (#609).
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