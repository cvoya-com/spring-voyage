// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Tests;

using System.Text.Json;
using Cvoya.Spring.Cli.Output;
using FluentAssertions;
using Xunit;

public class OutputFormatterTests
{
    [Fact]
    public void FormatTable_ArrayData_ProducesAlignedColumns()
    {
        var json = JsonSerializer.Deserialize<JsonElement>("""
            [
                { "id": "a1", "name": "Alice" },
                { "id": "b2", "name": "Bob" }
            ]
            """);

        var result = OutputFormatter.FormatTable(json, ["id", "name"]);

        result.Should().Contain("ID");
        result.Should().Contain("NAME");
        result.Should().Contain("a1");
        result.Should().Contain("Alice");
        result.Should().Contain("b2");
        result.Should().Contain("Bob");

        // Verify alignment: header and separator lines should have consistent structure
        var lines = result.Split(Environment.NewLine);
        lines.Length.Should().BeGreaterThanOrEqualTo(3); // header + separator + at least 1 row
        lines[1].Should().MatchRegex(@"^-+\s+-+$"); // separator line
    }

    [Fact]
    public void FormatTable_SingleObject_TreatedAsOneRowTable()
    {
        var json = JsonSerializer.Deserialize<JsonElement>("""
            { "id": "x1", "name": "Xander" }
            """);

        var result = OutputFormatter.FormatTable(json, ["id", "name"]);

        result.Should().Contain("x1");
        result.Should().Contain("Xander");
    }

    [Fact]
    public void FormatTable_EmptyArray_ReturnsNoResultsMessage()
    {
        var json = JsonSerializer.Deserialize<JsonElement>("[]");

        var result = OutputFormatter.FormatTable(json, ["id", "name"]);

        result.Should().Be("No results found.");
    }

    [Fact]
    public void FormatJson_ProducesValidIndentedJson()
    {
        var json = JsonSerializer.Deserialize<JsonElement>("""
            { "id": "test", "value": 42 }
            """);

        var result = OutputFormatter.FormatJson(json);

        // Should be valid JSON
        var reparsed = JsonSerializer.Deserialize<JsonElement>(result);
        reparsed.GetProperty("id").GetString().Should().Be("test");
        reparsed.GetProperty("value").GetInt32().Should().Be(42);

        // Should be indented (contain newlines)
        result.Should().Contain(Environment.NewLine);
    }

    [Fact]
    public void FormatTable_MissingProperty_ShowsEmptyString()
    {
        var json = JsonSerializer.Deserialize<JsonElement>("""
            [{ "id": "a1" }]
            """);

        var result = OutputFormatter.FormatTable(json, ["id", "name"]);

        result.Should().Contain("a1");
        // "name" column should exist in header but value should be empty
        result.Should().Contain("NAME");
    }
}
