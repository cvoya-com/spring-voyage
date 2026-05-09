// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Secrets;

/// <summary>
/// Deterministic AES-256 key shared by tests that need the
/// <see cref="Cvoya.Spring.Dapr.Secrets.SecretsEncryptor"/> to round-trip
/// real ciphertexts (instead of going through a substituted
/// <c>ISecretsEncryptor</c>). The platform no longer ships an in-memory
/// "ephemeral dev key" path — every encryptor instance now requires a
/// real key, so tests that exercise the encryptor end-to-end must supply
/// one too. This constant is set into <c>SPRING_SECRETS_AES_KEY</c> for
/// the duration of those tests via the existing
/// <see cref="SecretsEnvironmentVariableCollection"/> serialisation.
/// The value is a freshly-generated random 32-byte base64 string,
/// non-weak by the classifier's standards, and is irrelevant outside
/// the test process.
/// </summary>
internal static class SecretsTestKey
{
    public const string Base64 = "8w7eyN4Jf1g3AX9BPkej9gV2hWV1LO6lWIvs6RRQcAw=";
}
