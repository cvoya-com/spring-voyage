// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Auth;

using Microsoft.AspNetCore.Http;

/// <summary>
/// Centralised <see cref="IResult"/> factory for the
/// <see cref="UnitGitHubConfigRequest.AppInstallationId"/> /
/// <see cref="UnitGitHubConfigRequest.PatSecretName"/> "exactly one of"
/// invariant per ADR-0047 §11. Surfaces as a 400 ProblemDetails with a
/// structured <c>code</c> extension the CLI / portal pattern-match on.
/// </summary>
/// <remarks>
/// <para>
/// Per ADR-0047 §11 the binding row pins one outbound credential at
/// create time. Both null is rejected (no credential → outbound writes
/// impossible). Both set is rejected (the binding-create endpoint refuses
/// to silently pick one). The structured codes let the portal wizard's
/// auth-choice step and the CLI's <c>--installation-id</c> /
/// <c>--pat-secret-name</c> mutex render the same error the API surfaces.
/// </para>
/// <para>
/// Lives in the GitHub connector project because both shapes are
/// GitHub-specific: future connectors that grow their own binding-auth
/// surfaces contribute their own error vocabulary. The platform layer
/// does not enumerate the codes.
/// </para>
/// </remarks>
public static class GitHubBindingAuthProblems
{
    /// <summary>
    /// Stable URI placed into <c>type</c> on every binding-auth problem
    /// response. Mirrors the <c>DisplayNameProblems</c> pattern in
    /// <c>Host.Api</c> so the CLI and portal can pattern-match on the
    /// structured <c>code</c> extension regardless of which endpoint
    /// emitted the 400.
    /// </summary>
    public const string ProblemType =
        "https://docs.cvoya.com/spring/errors/github-binding-auth";

    /// <summary>
    /// Structured <c>code</c> extension value returned when neither
    /// <see cref="UnitGitHubConfigRequest.AppInstallationId"/> nor
    /// <see cref="UnitGitHubConfigRequest.PatSecretName"/> was supplied.
    /// </summary>
    public const string AuthRequiredCode = "GitHubBindingAuthRequired";

    /// <summary>
    /// Structured <c>code</c> extension value returned when both
    /// <see cref="UnitGitHubConfigRequest.AppInstallationId"/> AND
    /// <see cref="UnitGitHubConfigRequest.PatSecretName"/> were supplied.
    /// </summary>
    public const string AuthAmbiguousCode = "GitHubBindingAuthAmbiguous";

    /// <summary>
    /// Returns a 400 problem-details response carrying the
    /// "neither set" structured code. Surfaced when an operator submits a
    /// binding-create / update payload without an <c>appInstallationId</c>
    /// AND without a <c>patSecretName</c>.
    /// </summary>
    public static IResult AuthRequired() =>
        Results.Problem(
            type: ProblemType,
            title: "GitHub binding is missing an auth choice",
            detail: "Exactly one of 'appInstallationId' or 'patSecretName' " +
                    "is required on the GitHub connector binding. " +
                    "Provide an App installation id (for repos the SV App " +
                    "is installed on) or a tenant secret name addressing a " +
                    "personal access token.",
            statusCode: StatusCodes.Status400BadRequest,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = AuthRequiredCode,
            });

    /// <summary>
    /// Returns a 400 problem-details response carrying the "both set"
    /// structured code. Surfaced when an operator submits a binding-
    /// create / update payload with BOTH <c>appInstallationId</c> AND
    /// <c>patSecretName</c>.
    /// </summary>
    public static IResult AuthAmbiguous() =>
        Results.Problem(
            type: ProblemType,
            title: "GitHub binding has an ambiguous auth choice",
            detail: "Only one of 'appInstallationId' or 'patSecretName' " +
                    "may be supplied on the GitHub connector binding. The " +
                    "binding's outbound credential is pinned at create time; " +
                    "the endpoint refuses to silently pick one of the two.",
            statusCode: StatusCodes.Status400BadRequest,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = AuthAmbiguousCode,
            });

    /// <summary>
    /// Validates the
    /// <see cref="UnitGitHubConfigRequest.AppInstallationId"/> /
    /// <see cref="UnitGitHubConfigRequest.PatSecretName"/> pair and
    /// returns a 400 response when the exactly-one-of rule is violated;
    /// returns <c>null</c> when the request is acceptable. Centralised
    /// here so the typed-config endpoint and any future binding-create
    /// surfaces use one validator.
    /// </summary>
    /// <param name="appInstallationId">The candidate App installation id.</param>
    /// <param name="patSecretName">The candidate PAT secret name.</param>
    public static IResult? ValidateOrProblem(long? appInstallationId, string? patSecretName)
    {
        var hasApp = appInstallationId is > 0;
        var hasPat = !string.IsNullOrWhiteSpace(patSecretName);

        if (!hasApp && !hasPat)
        {
            return AuthRequired();
        }
        if (hasApp && hasPat)
        {
            return AuthAmbiguous();
        }
        return null;
    }
}
