// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

#nullable disable

namespace Cvoya.Spring.Dapr.Data.Migrations;

using Microsoft.EntityFrameworkCore.Migrations;

/// <inheritdoc />
public partial class ExtendHumansWithPermissionAndPreferences : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "notification_preferences",
            schema: "spring",
            table: "humans",
            type: "jsonb",
            nullable: true);

        // PermissionLevel.Operator (= 1) is the OSS interim default per
        // ADR-0040 / #1473 / #1479. Existing humans rows pick up
        // Operator on column add so the new-conversation round-trip
        // continues to work without a separate promotion step.
        migrationBuilder.AddColumn<int>(
            name: "permission_level",
            schema: "spring",
            table: "humans",
            type: "integer",
            nullable: false,
            defaultValue: 1);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "notification_preferences",
            schema: "spring",
            table: "humans");

        migrationBuilder.DropColumn(
            name: "permission_level",
            schema: "spring",
            table: "humans");
    }
}
