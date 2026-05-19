// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Auth;

/// <summary>
/// Raised by <see cref="GitHubBindingAuthResolver.ResolveAsync"/> when the
/// binding's pinned credential cannot be materialised at outbound-call
/// time per ADR-0047 §6. Concretely:
/// <list type="bullet">
///   <item><description>
///     The binding's <c>PatSecretName</c> is set but
///     <see cref="Cvoya.Spring.Core.Secrets.ISecretResolver"/> reports
///     <see cref="Cvoya.Spring.Core.Secrets.SecretResolvePath.NotFound"/>.
///   </description></item>
///   <item><description>
///     The binding's <c>AppInstallationId</c> is set but the GitHub App
///     installation token mint failed (network, 401, 403, 5xx). The inner
///     exception carries the upstream failure for log-side diagnosis.
///   </description></item>
///   <item><description>
///     Defensive assertion: neither <c>AppInstallationId</c> nor
///     <c>PatSecretName</c> is set on the binding. The binding-create gate
///     (ADR-0047 §11) prevents this state from being persisted, so reaching
///     this branch is a wiring bug rather than an operator error — but the
///     resolver still raises rather than dereferences a null credential.
///   </description></item>
/// </list>
/// Carries a stable <see cref="Code"/> the CLI / portal pattern-match on
/// (<c>GitHubBindingAuthMissing</c>) so the wire envelope is identical to
/// the binding-create-time codes (<c>GitHubBindingAuthRequired</c> /
/// <c>GitHubBindingAuthAmbiguous</c>); only the moment of detection
/// changes between create-time and use-time.
/// </summary>
public sealed class GitHubBindingAuthMissingException : InvalidOperationException
{
    /// <summary>
    /// Stable structured code surfaced to the CLI / portal. Matches the
    /// <c>code</c> extension <see cref="GitHubBindingAuthProblems"/> emits
    /// on the binding-create-time problem responses.
    /// </summary>
    public const string CodeValue = "GitHubBindingAuthMissing";

    /// <summary>
    /// Structured code echoed verbatim into logs and any wire envelope a
    /// caller chooses to translate this exception into.
    /// </summary>
    public string Code { get; } = CodeValue;

    /// <summary>
    /// Initialises the exception with a human-readable message. The
    /// structured <see cref="Code"/> is always <see cref="CodeValue"/>.
    /// </summary>
    public GitHubBindingAuthMissingException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initialises the exception with a human-readable message and the
    /// upstream failure that triggered it (e.g. an Octokit
    /// <c>ApiException</c> from the App-installation token mint, or a
    /// secret-store transport failure).
    /// </summary>
    public GitHubBindingAuthMissingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
