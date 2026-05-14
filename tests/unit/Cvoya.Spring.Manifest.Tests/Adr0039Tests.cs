// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Manifest.Tests;

using Shouldly;

using Xunit;

/// <summary>
/// Parser tests for ADR-0039 legacy container-runtime removal.
/// </summary>
public class Adr0039Tests
{
    [Fact]
    public void Parse_RootContainerRuntime_ThrowsLegacyContainerRuntimeField()
    {
        // Arrange
        var yaml = """
            name: my-unit
            containerRuntime: docker
            """;

        // Act
        var ex = Should.Throw<ManifestParseException>(() => ManifestParser.Parse(yaml));

        // Assert
        ex.Message.ShouldContain("LegacyContainerRuntimeField");
    }

    [Fact]
    public void Parse_ExecutionContainerRuntime_ThrowsLegacyContainerRuntimeField()
    {
        // Arrange
        var yaml = """
            name: my-unit
            execution:
              containerRuntime: podman
              runtime: claude-code
            """;

        // Act
        var ex = Should.Throw<ManifestParseException>(() => ManifestParser.Parse(yaml));

        // Assert
        ex.Message.ShouldContain("LegacyContainerRuntimeField");
    }
}
