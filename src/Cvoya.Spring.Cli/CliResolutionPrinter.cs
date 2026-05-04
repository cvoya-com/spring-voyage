// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli;

using System.IO;

/// <summary>
/// Renders <see cref="CliResolutionException"/> output to a writer (stderr
/// in production; an in-memory writer in tests). Lives outside
/// <see cref="CliResolver"/> so the resolver itself stays pure — the
/// caller controls exit-code semantics and where bytes go.
/// </summary>
public static class CliResolutionPrinter
{
    /// <summary>
    /// Writes the human-readable diagnostic for a resolver failure.
    ///
    /// 0-match: a single error line.
    /// n-match: a header line, an indented candidate list, and a
    /// re-run hint pointing at the first Guid as an example.
    ///
    /// Output is written to <paramref name="writer"/>; production code
    /// passes <see cref="System.Console.Error"/>. The exact byte shape
    /// is what the integration tests assert on, so we keep this in one
    /// place.
    /// </summary>
    public static void Write(TextWriter writer, CliResolutionException error)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(error);

        var noun = error.Kind switch
        {
            CliEntityKind.Agent => "agent",
            CliEntityKind.Unit => "unit",
            _ => "entity",
        };

        var contextSuffix = error.Context is System.Guid g
            ? $" in unit '{Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(g)}'"
            : string.Empty;

        if (error.Candidates.Count == 0)
        {
            writer.WriteLine($"No {noun} found matching '{error.Query}'{contextSuffix}.");
            return;
        }

        // n-match: list every candidate so the operator can copy/paste a Guid.
        writer.WriteLine($"Multiple {noun}s match '{error.Query}'{contextSuffix}. Specify by id:");
        writer.WriteLine();
        foreach (var candidate in error.Candidates)
        {
            var id = Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(candidate.Id);
            var parent = string.IsNullOrEmpty(candidate.ParentContext)
                ? string.Empty
                : $" ({candidate.ParentContext})";
            writer.WriteLine($"  {id}  {candidate.DisplayName}{parent}");
        }
        writer.WriteLine();
        var firstId = Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(error.Candidates[0].Id);
        writer.WriteLine("Re-run with the desired id, e.g.:");
        writer.WriteLine($"  spring {noun} show {firstId}");
    }
}