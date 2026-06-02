// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

#nullable disable

namespace Cvoya.Spring.Dapr.Data.Migrations;

using Microsoft.EntityFrameworkCore.Migrations;

/// <inheritdoc />
public partial class LifecycleStatusMirror : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "lifecycle_status",
            schema: "spring",
            table: "unit_live_config",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<int>(
            name: "lifecycle_status",
            schema: "spring",
            table: "agent_live_config",
            type: "integer",
            nullable: false,
            defaultValue: 0);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "lifecycle_status",
            schema: "spring",
            table: "unit_live_config");

        migrationBuilder.DropColumn(
            name: "lifecycle_status",
            schema: "spring",
            table: "agent_live_config");
    }
}
