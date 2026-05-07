// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Catalog;

/// <summary>
/// Canonical credential-secret naming helpers for ADR-0038 §
/// "Credential identity". A tenant's stored LLM credential is keyed on
/// <c>(tenant, provider, authMethod)</c>; the persisted secret name is
/// <c>{provider}-{authMethod-slug}</c>. This is the single seam every
/// resolver / launcher / endpoint reads for the canonical secret name —
/// hard-coded constants would silently drift across surfaces.
/// </summary>
public static class CredentialNaming
{
    /// <summary>
    /// Returns the canonical secret name for a
    /// <c>(provider, authMethod)</c> edge — for example,
    /// <c>("anthropic", AuthMethod.Oauth)</c> →
    /// <c>"anthropic-oauth"</c>.
    /// </summary>
    /// <param name="providerId">The provider id (e.g. <c>anthropic</c>, <c>openai</c>).</param>
    /// <param name="authMethod">The auth method on the edge.</param>
    /// <returns>Lowercase <c>{provider}-{authMethod-slug}</c>.</returns>
    public static string SecretNameFor(string providerId, AuthMethod authMethod)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        var slug = authMethod switch
        {
            AuthMethod.Oauth => "oauth",
            AuthMethod.ApiKey => "api-key",
            _ => throw new ArgumentOutOfRangeException(nameof(authMethod)),
        };

        return $"{providerId.ToLowerInvariant()}-{slug}";
    }
}