// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Manifest.Tests;

using System;
using System.Collections.Generic;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for <see cref="PackageManifestParser"/> input validation and
/// <c>${{ inputs.* }}</c> substitution (ADR-0035 decision 8).
/// </summary>
public class InputValidationTests
{
    // ---- ValidateInputs -------------------------------------------------

    [Fact]
    public void ValidateInputs_RequiredInputMissing_Throws()
    {
        var schema = new List<PackageInputDefinition>
        {
            new() { Name = "team_name", Type = "string", Required = true }
        };

        var ex = Should.Throw<PackageInputValidationException>(
            () => PackageManifestParser.ValidateInputs(schema, new Dictionary<string, string>()));

        ex.InputName.ShouldBe("team_name");
        ex.Message.ShouldContain("team_name");
        ex.Message.ShouldContain("required");
    }

    [Fact]
    public void ValidateInputs_RequiredInputPresent_DoesNotThrow()
    {
        var schema = new List<PackageInputDefinition>
        {
            new() { Name = "team_name", Type = "string", Required = true }
        };
        var values = new Dictionary<string, string> { ["team_name"] = "Engineering" };

        // Should not throw.
        PackageManifestParser.ValidateInputs(schema, values);
    }

    [Fact]
    public void ValidateInputs_RequiredInputWithDefault_NoValueSupplied_UsesDefault()
    {
        // A required input with a default and no supplied value uses the default rather
        // than throwing. The "required" flag communicates that the input is meaningful
        // and must be considered; the default provides the fallback value if none is given.
        var schema = new List<PackageInputDefinition>
        {
            new() { Name = "team_name", Type = "string", Required = true, Default = "DefaultTeam" }
        };

        // Should not throw — the default covers the missing value.
        PackageManifestParser.ValidateInputs(schema, new Dictionary<string, string>());
    }

    [Fact]
    public void ValidateInputs_IntTypeMismatch_Throws()
    {
        var schema = new List<PackageInputDefinition>
        {
            new() { Name = "replica_count", Type = "int", Required = false }
        };
        var values = new Dictionary<string, string> { ["replica_count"] = "not-a-number" };

        var ex = Should.Throw<PackageInputValidationException>(
            () => PackageManifestParser.ValidateInputs(schema, values));

        ex.InputName.ShouldBe("replica_count");
        ex.Message.ShouldContain("int");
        ex.Message.ShouldContain("not-a-number");
    }

    [Fact]
    public void ValidateInputs_IntTypeValidValue_Succeeds()
    {
        var schema = new List<PackageInputDefinition>
        {
            new() { Name = "replica_count", Type = "int", Required = false }
        };
        var values = new Dictionary<string, string> { ["replica_count"] = "3" };

        // Should not throw.
        PackageManifestParser.ValidateInputs(schema, values);
    }

    [Fact]
    public void ValidateInputs_BoolTypeMismatch_Throws()
    {
        var schema = new List<PackageInputDefinition>
        {
            new() { Name = "enabled", Type = "bool", Required = false }
        };
        var values = new Dictionary<string, string> { ["enabled"] = "yes-please" };

        var ex = Should.Throw<PackageInputValidationException>(
            () => PackageManifestParser.ValidateInputs(schema, values));

        ex.InputName.ShouldBe("enabled");
        ex.Message.ShouldContain("bool");
        ex.Message.ShouldContain("yes-please");
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("True")]
    [InlineData("False")]
    public void ValidateInputs_BoolTypeValidValues_Succeed(string value)
    {
        var schema = new List<PackageInputDefinition>
        {
            new() { Name = "enabled", Type = "bool", Required = false }
        };
        var values = new Dictionary<string, string> { ["enabled"] = value };

        // Should not throw.
        PackageManifestParser.ValidateInputs(schema, values);
    }

    [Fact]
    public void ValidateInputs_SecretTyped_SkipsTypeValidation()
    {
        // Secret inputs are not type-checked — the caller supplies a secret reference.
        var schema = new List<PackageInputDefinition>
        {
            new() { Name = "api_key", Type = "string", Secret = true, Required = false }
        };
        var values = new Dictionary<string, string> { ["api_key"] = "secret://my-tenant/api-key" };

        // Should not throw (secret inputs skip type validation).
        PackageManifestParser.ValidateInputs(schema, values);
    }

    [Fact]
    public void ValidateInputs_EmptySchema_Succeeds()
    {
        // No schema → no validation → succeed even with extra values.
        PackageManifestParser.ValidateInputs(null, new Dictionary<string, string> { ["x"] = "y" });
        PackageManifestParser.ValidateInputs([], new Dictionary<string, string> { ["x"] = "y" });
    }

    // ---- SubstituteInputs -----------------------------------------------

    [Fact]
    public void SubstituteInputs_SingleValue_Replaced()
    {
        var schema = new List<PackageInputDefinition>
        {
            new() { Name = "team_name", Type = "string" }
        };
        var values = new Dictionary<string, string> { ["team_name"] = "Engineering" };
        var yaml = "description: A package for ${{ inputs.team_name }}.";

        var result = PackageManifestParser.SubstituteInputs(yaml, schema, values);

        result.ShouldBe("description: A package for Engineering.");
    }

    [Fact]
    public void SubstituteInputs_MultipleDistinctValues_AllReplaced()
    {
        var schema = new List<PackageInputDefinition>
        {
            new() { Name = "pkg_name", Type = "string" },
            new() { Name = "team_name", Type = "string" },
        };
        var values = new Dictionary<string, string>
        {
            ["pkg_name"] = "my-pkg",
            ["team_name"] = "Engineering",
        };
        var yaml = "name: ${{ inputs.pkg_name }}\ndescription: for ${{ inputs.team_name }}";

        var result = PackageManifestParser.SubstituteInputs(yaml, schema, values);

        result.ShouldBe("name: my-pkg\ndescription: for Engineering");
    }

    [Fact]
    public void SubstituteInputs_RepeatedToken_AllInstancesReplaced()
    {
        var schema = new List<PackageInputDefinition>
        {
            new() { Name = "name", Type = "string" }
        };
        var values = new Dictionary<string, string> { ["name"] = "acme" };
        var yaml = "${{ inputs.name }}/${{ inputs.name }}";

        var result = PackageManifestParser.SubstituteInputs(yaml, schema, values);

        result.ShouldBe("acme/acme");
    }

    [Fact]
    public void SubstituteInputs_UndeclaredInput_Throws()
    {
        var schema = new List<PackageInputDefinition>
        {
            new() { Name = "team_name", Type = "string" }
        };
        var values = new Dictionary<string, string> { ["team_name"] = "Eng" };
        var yaml = "${{ inputs.undeclared_thing }}";

        var ex = Should.Throw<PackageInputValidationException>(
            () => { PackageManifestParser.SubstituteInputs(yaml, schema, values); });

        ex.InputName.ShouldBe("undeclared_thing");
        ex.Message.ShouldContain("undeclared_thing");
    }

    [Fact]
    public void SubstituteInputs_SecretInput_StoresSecretReference()
    {
        var schema = new List<PackageInputDefinition>
        {
            new() { Name = "api_key", Type = "string", Secret = true }
        };
        // The caller passes a secret reference.
        var values = new Dictionary<string, string> { ["api_key"] = "secret://tenant/api-key" };
        var yaml = "connector_token: ${{ inputs.api_key }}";

        var result = PackageManifestParser.SubstituteInputs(yaml, schema, values);

        result.ShouldBe("connector_token: secret://tenant/api-key");
    }

    [Fact]
    public void SubstituteInputs_DefaultAppliedWhenNotSupplied()
    {
        var schema = new List<PackageInputDefinition>
        {
            new() { Name = "replica_count", Type = "int", Default = "2" }
        };
        var values = new Dictionary<string, string>(); // not supplied.
        var yaml = "replicas: ${{ inputs.replica_count }}";

        var result = PackageManifestParser.SubstituteInputs(yaml, schema, values);

        result.ShouldBe("replicas: 2");
    }

    [Fact]
    public void SubstituteInputs_NoTokensInYaml_ReturnsUnchanged()
    {
        var schema = new List<PackageInputDefinition>();
        var values = new Dictionary<string, string>();
        var yaml = "kind: UnitPackage\nmetadata:\n  name: static-pkg";

        var result = PackageManifestParser.SubstituteInputs(yaml, schema, values);

        result.ShouldBe(yaml);
    }

    [Fact]
    public void SubstituteInputs_WithSpacesAroundInputName_Replaced()
    {
        // The pattern allows optional whitespace: ${{ inputs.name }}
        var schema = new List<PackageInputDefinition>
        {
            new() { Name = "x", Type = "string" }
        };
        var values = new Dictionary<string, string> { ["x"] = "hello" };
        var yaml = "${{  inputs.x  }}";

        var result = PackageManifestParser.SubstituteInputs(yaml, schema, values);

        result.ShouldBe("hello");
    }
}