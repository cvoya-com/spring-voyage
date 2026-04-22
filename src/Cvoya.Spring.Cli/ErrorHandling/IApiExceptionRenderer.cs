// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.ErrorHandling;

using Microsoft.Kiota.Abstractions;

/// <summary>
/// Renders a Kiota <see cref="ApiException"/> for the CLI in either prose
/// or JSON form. Provided as a swap point so the cloud host can replace
/// the OSS rendering without forking the command bodies — register a
/// custom implementation by assigning
/// <see cref="ApiExceptionRenderer.Instance"/> from the host's
/// <c>Main</c> before <c>Program.Main</c> dispatches the parsed command.
/// </summary>
public interface IApiExceptionRenderer
{
    /// <summary>
    /// Writes the rendered error to the appropriate output stream
    /// (stderr for prose, stdout for JSON to keep <c>--output json</c>
    /// pipeable into <c>jq</c>) and returns the process exit code that
    /// the caller should propagate.
    /// </summary>
    int Render(ApiException exception, CliRenderContext context);
}