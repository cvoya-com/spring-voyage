// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

#nullable disable

namespace Cvoya.Spring.Dapr.Data.Migrations;

using Microsoft.EntityFrameworkCore.Migrations;

/// <inheritdoc />
public partial class UnitMetadataParity : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "enabled",
            schema: "spring",
            table: "unit_live_config",
            type: "boolean",
            nullable: false,
            defaultValue: true);

        migrationBuilder.AddColumn<int>(
            name: "execution_mode",
            schema: "spring",
            table: "unit_live_config",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<string>(
            name: "specialty",
            schema: "spring",
            table: "unit_live_config",
            type: "character varying(256)",
            maxLength: 256,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "role",
            schema: "spring",
            table: "unit_definitions",
            type: "character varying(512)",
            maxLength: 512,
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "enabled",
            schema: "spring",
            table: "unit_live_config");

        migrationBuilder.DropColumn(
            name: "execution_mode",
            schema: "spring",
            table: "unit_live_config");

        migrationBuilder.DropColumn(
            name: "specialty",
            schema: "spring",
            table: "unit_live_config");

        migrationBuilder.DropColumn(
            name: "role",
            schema: "spring",
            table: "unit_definitions");
    }
}
