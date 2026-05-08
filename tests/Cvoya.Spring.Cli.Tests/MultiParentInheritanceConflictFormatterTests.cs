// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Tests;

using Cvoya.Spring.Cli.ErrorHandling;
using Cvoya.Spring.Cli.Generated.Models;

using Shouldly;

using Xunit;

public class MultiParentInheritanceConflictFormatterTests
{
    [Fact]
    public void FormatLines_SourceShape_PrintsOneLinePerConflictingField()
    {
        using var fields = System.Text.Json.JsonDocument.Parse(
            """
            {
              "runtime": [
                { "source": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "value": "claude-code" },
                { "source": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "value": "spring-voyage" }
              ]
            }
            """);
        var problem = new ProblemDetails
        {
            AdditionalData = new Dictionary<string, object>
            {
                ["error"] = "MultiParentInheritanceConflict",
                ["conflictingFields"] = fields.RootElement.Clone(),
            },
        };
        problem.ResponseStatusCode = 422;

        MultiParentInheritanceConflictFormatter.TryParse(problem, out var conflict).ShouldBeTrue();
        var lines = MultiParentInheritanceConflictFormatter.FormatLines(
            conflict,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"] = "unit-engineering",
                ["bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"] = "unit-support",
            });

        lines.ShouldBe(new[]
        {
            "runtime: unit-engineering=claude-code, unit-support=spring-voyage",
        });
    }
}