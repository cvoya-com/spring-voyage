// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Utilities;

using System;

using Cvoya.Spring.Cli.Generated.Models;

/// <summary>
/// Compatibility entry point for command catch-sites that predate
/// <see cref="ProblemDetailsTranslator"/>. It now delegates to the shared
/// translator so all CLI surfaces use the same ProblemDetails copy.
/// </summary>
public static class ProblemDetailsFormatter
{
    public static string Format(ProblemDetails problem)
    {
        ArgumentNullException.ThrowIfNull(problem);
        return ProblemDetailsTranslator.TranslateProblemDetails(problem);
    }

    public static string Format(Exception exception)
    {
        return ProblemDetailsTranslator.Format(exception);
    }
}
