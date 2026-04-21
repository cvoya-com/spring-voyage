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
/// <param name="Code">
/// Optional, machine-readable failure category. Lets UI surfaces (the
/// portal wizard, the CLI) substitute a context-aware message — e.g.
/// naming the chosen container image — instead of echoing the runtime's
/// generic <see cref="ErrorMessage"/>. <c>null</c> when the failure has
/// no special UI treatment, when the result is successful, or for legacy
/// runtimes that have not been updated. Known values are documented as
/// <see cref="CredentialValidationCodes"/> constants; the wire format is
/// a string so the private cloud host (and future runtimes) can add new
/// codes without changing this enum.
/// </param>
public sealed record CredentialValidationResult(
    bool Valid,
    string? ErrorMessage,
    CredentialValidationStatus Status,
    string? Code = null);

/// <summary>
/// Well-known string values for <see cref="CredentialValidationResult.Code"/>.
/// Surfaces (portal wizard, CLI) match on these codes to render
/// context-aware guidance (e.g. naming the chosen agent image) instead of
/// the runtime's generic <see cref="CredentialValidationResult.ErrorMessage"/>.
/// </summary>
public static class CredentialValidationCodes
{
    /// <summary>
    /// The runtime requires a host-side prerequisite (e.g. a CLI binary) that
    /// is not present, so this credential format cannot be validated. The
    /// canonical example is the Claude runtime when handed a Claude.ai OAuth
    /// token (<c>sk-ant-oat…</c>) without the <c>claude</c> CLI on PATH —
    /// the OAuth path has no REST fallback. UI surfaces use this code to
    /// substitute messaging that names the chosen container image and the
    /// missing tool.
    /// </summary>
    public const string BaselineUnavailable = "BaselineUnavailable";
}