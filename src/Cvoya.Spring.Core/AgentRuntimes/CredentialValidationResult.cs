// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.AgentRuntimes;

/// <summary>
/// The result of validating a candidate credential against a runtime's
/// backing service.
/// </summary>
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
public sealed record CredentialValidationResult(
    bool Valid,
    string? ErrorMessage,
    CredentialValidationStatus Status);