// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.ModelProviders;

/// <summary>
/// The result of validating a candidate credential against a model
/// provider's backing service.
/// </summary>
/// <remarks>
/// Intentionally not <c>sealed</c> so private-repo providers / connector
/// types can extend the shape with provider-specific diagnostic fields
/// (e.g. throttling reasons) without forking the open-source contract.
/// </remarks>
/// <param name="Valid">
/// Convenience flag: <c>true</c> only when <paramref name="Status"/> is
/// <see cref="CredentialValidationStatus.Valid"/>. Callers that care about
/// the <em>reason</em> a credential was not accepted should inspect
/// <paramref name="Status"/> and <paramref name="ErrorMessage"/> directly.
/// </param>
/// <param name="ErrorMessage">
/// Human-readable explanation when the credential was not accepted or the
/// check could not complete. <c>null</c> on success.
/// </param>
/// <param name="Status">The raw outcome of this validation attempt.</param>
/// <param name="ValidatedAt">
/// Wall-clock timestamp of the probe attempt. Defaults to <c>null</c> so
/// existing callers (and providers that haven't been updated to surface
/// a timestamp) keep compiling; the host's
/// <c>POST /api/v1/tenant/model-providers/installs/{id}/validate-credential</c>
/// endpoint substitutes <see cref="DateTimeOffset.UtcNow"/> when this is
/// null so the wire DTO and the persisted
/// <c>credential_health.LastChecked</c> row always carry a value
/// (#1066).
/// </param>
public record CredentialValidationResult(
    bool Valid,
    string? ErrorMessage,
    CredentialValidationStatus Status,
    DateTimeOffset? ValidatedAt = null);