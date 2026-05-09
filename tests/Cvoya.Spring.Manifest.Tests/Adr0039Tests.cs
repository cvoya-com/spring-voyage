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
        var yaml = """
            apiVersion: spring.voyage/v1
            kind: Unit
            name: research
            description: Research unit
            containerRuntime: podman
            """;

        var ex = Should.Throw<ManifestParseException>(() => ManifestParser.Parse(yaml));

        ex.Message.ShouldContain("LegacyContainerRuntimeField");
    }

    [Fact]
    public void Parse_ExecutionContainerRuntime_ThrowsLegacyContainerRuntimeField()
    {
        var yaml = """
            apiVersion: spring.voyage/v1
            kind: Unit
            name: research
            description: Research unit
            execution:
              image: ghcr.io/example/agent:latest
              containerRuntime: podman
            """;

        var ex = Should.Throw<ManifestParseException>(() => ManifestParser.Parse(yaml));

        ex.Message.ShouldContain("LegacyContainerRuntimeField");
    }
}
