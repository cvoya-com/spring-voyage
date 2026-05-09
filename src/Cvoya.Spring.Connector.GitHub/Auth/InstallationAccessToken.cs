// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Auth;

/// <summary>
/// A GitHub App installation access token together with its server-reported
/// expiration timestamp. The cache keeps the real <see cref="ExpiresAt"/> rather
/// than a conservative "minimum" so proactive refresh can be driven off the
/// actual TTL GitHub issued.
/// </summary>
/// <param name="Token">The bearer token string.</param>
/// <param name="ExpiresAt">The UTC instant at which GitHub will reject the token.</param>
public readonly record struct InstallationAccessToken(string Token, DateTimeOffset ExpiresAt);
