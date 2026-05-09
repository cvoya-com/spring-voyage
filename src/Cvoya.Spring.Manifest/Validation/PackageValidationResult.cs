// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Manifest.Validation;

using System.Collections.Generic;
using System.Linq;

/// <summary>Severity of a single <see cref="PackageValidationDiagnostic"/>.</summary>
public enum PackageValidationSeverity
{
    /// <summary>An advisory finding. Promoted to <see cref="Error"/> under <c>--strict</c>.</summary>
    Warning,

    /// <summary>A blocking finding — the package would not install cleanly.</summary>
    Error,
}

/// <summary>
/// One finding produced by <see cref="PackageValidator"/>. Carries enough
/// detail for the CLI to render a human-readable line and for CI to emit a
/// GitHub file annotation.
/// </summary>
/// <param name="File">Relative path (forward-slash) of the offending file inside the package.</param>
/// <param name="Severity">The diagnostic's severity.</param>
/// <param name="Code">Short stable identifier for the diagnostic kind (e.g. <c>unit-missing-image</c>).</param>
/// <param name="Message">Human-readable diagnostic message.</param>
public sealed record PackageValidationDiagnostic(
    string File,
    PackageValidationSeverity Severity,
    string Code,
    string Message);

/// <summary>
/// The full result of a <see cref="PackageValidator"/> run.
/// <see cref="Diagnostics"/> is grouped per-file; <see cref="Files"/> lists
/// every file the validator inspected (including ones that produced no
/// findings) so callers can render an "ok" line for every file as the
/// brief format requires.
/// </summary>
public sealed class PackageValidationResult
{
    /// <summary>Relative paths the validator visited, in iteration order.</summary>
    public required IReadOnlyList<string> Files { get; init; }

    /// <summary>Diagnostics produced during the run.</summary>
    public required IReadOnlyList<PackageValidationDiagnostic> Diagnostics { get; init; }

    /// <summary>Count of <see cref="PackageValidationSeverity.Error"/> diagnostics.</summary>
    public int ErrorCount => Diagnostics.Count(d => d.Severity == PackageValidationSeverity.Error);

    /// <summary>Count of <see cref="PackageValidationSeverity.Warning"/> diagnostics.</summary>
    public int WarningCount => Diagnostics.Count(d => d.Severity == PackageValidationSeverity.Warning);

    /// <summary><c>true</c> when no errors were produced.</summary>
    public bool IsClean => ErrorCount == 0;
}
