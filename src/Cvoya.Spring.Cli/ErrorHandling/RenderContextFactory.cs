// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.ErrorHandling;

using System.CommandLine;

/// <summary>
/// Builds a <see cref="CliRenderContext"/> from the active
/// <see cref="ParseResult"/> by resolving the recursive root options
/// (<c>--output</c>, <c>--verbose</c>) by name. Lives here so each
/// command site doesn't need a reference to the option instances —
/// command modules already get the <c>outputOption</c> handle, but the
/// recursive lookup also lets future commands skip the wiring entirely.
/// </summary>
public static class RenderContextFactory
{
    /// <summary>
    /// Builds a render context anchored on the parse result. Unknown
    /// option values fall back to the safe defaults (<c>"table"</c>,
    /// <c>verbose=false</c>) so the renderer never throws while trying
    /// to render an exception.
    /// </summary>
    public static CliRenderContext For(ParseResult parseResult, string? operationDescription = null)
    {
        var output = TryGetString(parseResult, "--output") ?? "table";
        var verbose = TryGetBool(parseResult, "--verbose");
        return new CliRenderContext(output, verbose, operationDescription);
    }

    private static string? TryGetString(ParseResult parseResult, string name)
    {
        try
        {
            return parseResult.GetValue<string>(name);
        }
        catch
        {
            return null;
        }
    }

    private static bool TryGetBool(ParseResult parseResult, string name)
    {
        try
        {
            return parseResult.GetValue<bool>(name);
        }
        catch
        {
            return false;
        }
    }
}