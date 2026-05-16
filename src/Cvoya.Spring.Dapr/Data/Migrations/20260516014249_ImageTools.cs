// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

#nullable disable

namespace Cvoya.Spring.Dapr.Data.Migrations;

using System.Text.Json;

using Microsoft.EntityFrameworkCore.Migrations;

/// <inheritdoc />
public partial class ImageTools : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<JsonElement>(
            name: "image_tools",
            schema: "spring",
            table: "unit_definitions",
            type: "jsonb",
            nullable: true);

        migrationBuilder.AddColumn<JsonElement>(
            name: "image_tools",
            schema: "spring",
            table: "agent_definitions",
            type: "jsonb",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "image_tools",
            schema: "spring",
            table: "unit_definitions");

        migrationBuilder.DropColumn(
            name: "image_tools",
            schema: "spring",
            table: "agent_definitions");
    }
}
