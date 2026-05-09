// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli;

using Cvoya.Spring.Core.Units;

/// <summary>
/// Maps <see cref="UnitValidationCodes"/> values to integer process exit codes
/// for the <c>spring unit create</c> / <c>spring unit revalidate</c> commands.
/// Exposes the public CLI contract as data rather than scattering constants
/// across command handlers.
///
/// The exit-code table is an **additive-only** part of the CLI's public
/// surface. Never renumber an existing mapping. New codes extend with the
/// next unused number in the 20..29 range. Operators script against these
/// numbers; renumbering silently breaks every pipeline that branches on them.
/// </summary>
public static class UnitValidationExitCodes
{
    /// <summary>Success — unit reached a terminal passing state (typically <c>Stopped</c>).</summary>
    public const int Success = 0;

    /// <summary>Unknown or transport error (network, unexpected response, Ctrl+C, etc.).</summary>
    public const int UnknownError = 1;

    /// <summary>Usage error — bad flags, missing args, or 409 on <c>revalidate</c>.</summary>
    public const int UsageError = 2;

    /// <summary>
    /// Returns the process exit code for a given
    /// <see cref="UnitValidationCodes"/> string, or <see cref="UnknownError"/>
    /// (=<c>1</c>) when the code is null, unknown, or not yet registered in
    /// the table. The mapping is the canonical CLI contract — see the class
    /// remarks for the additive-only rule.
    /// </summary>
    /// <param name="code">
    /// One of the <see cref="UnitValidationCodes"/> string constants. Case-
    /// sensitive; the server emits the constant names verbatim.
    /// </param>
    /// <returns>
    /// The matching integer exit code, or <see cref="UnknownError"/> when the
    /// input does not match any registered validation code.
    /// </returns>
    public static int ForCode(string? code)
    {
        return code switch
        {
            UnitValidationCodes.ImagePullFailed => 20,
            UnitValidationCodes.ImageStartFailed => 21,
            UnitValidationCodes.ToolMissing => 22,
            UnitValidationCodes.CredentialInvalid => 23,
            UnitValidationCodes.CredentialFormatRejected => 24,
            UnitValidationCodes.ModelNotFound => 25,
            UnitValidationCodes.ProbeTimeout => 26,
            UnitValidationCodes.ProbeInternalError => 27,
            _ => UnknownError,
        };
    }

    /// <summary>
    /// The exit-code help table rendered at the end of <c>--help</c> output
    /// on <c>spring unit create</c> and <c>spring unit revalidate</c> so
    /// operators can read the full contract without leaving the terminal.
    /// Shared between both commands so the wording cannot drift.
    /// </summary>
    public const string HelpTable =
        "Exit codes:\n" +
        "  0   Success\n" +
        "  1   Unknown/other error\n" +
        "  2   Usage error (bad flags or invalid state for the operation)\n" +
        "  20  ImagePullFailed         22  ToolMissing         24  CredentialFormatRejected\n" +
        "  21  ImageStartFailed        23  CredentialInvalid   25  ModelNotFound\n" +
        "                              26  ProbeTimeout        27  ProbeInternalError\n" +
        "\n" +
        "Defaults to --wait (polls until terminal). Pass --no-wait to return immediately.";
}
