// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.ModelProviders;

/// <summary>
/// Tenant-scoped configuration for an installed model provider
/// (ADR-0038). Persisted as JSON on the install row and surfaced via
/// <c>/api/v1/tenant/model-providers/installs/{id}</c>.
/// </summary>
/// <param name="Models">
/// Model ids the tenant has enabled for this provider. When empty,
/// callers that need a list should fall back to the catalogue's
/// <c>defaultModels</c> for the provider.
/// </param>
/// <param name="DefaultModel">
/// Preferred model id — used by the wizard to pre-select a value. When
/// <c>null</c> the first entry of <paramref name="Models"/> is used.
/// </param>
/// <param name="BaseUrl">
/// Optional base URL override used by providers that support self-hosted
/// or proxied endpoints (e.g. Ollama, OpenAI-compatible gateways).
/// </param>
public sealed record ModelProviderInstallConfig(
    IReadOnlyList<string> Models,
    string? DefaultModel,
    string? BaseUrl)
{
    /// <summary>Empty config with no models, default, or base URL.</summary>
    public static readonly ModelProviderInstallConfig Empty =
        new(Array.Empty<string>(), null, null);
}
