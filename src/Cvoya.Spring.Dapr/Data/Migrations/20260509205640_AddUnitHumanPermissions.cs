// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Migrations;

using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

/// <inheritdoc />
public partial class AddUnitHumanPermissions : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "unit_human_permissions",
            schema: "spring",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                human_id = table.Column<Guid>(type: "uuid", nullable: false),
                permission_level = table.Column<int>(type: "integer", nullable: false),
                identity = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                notifications = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                granted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_unit_human_permissions", x => x.id);
            });

        migrationBuilder.CreateIndex(
            name: "ux_unit_human_permissions_tenant_unit_human",
            schema: "spring",
            table: "unit_human_permissions",
            columns: new[] { "tenant_id", "unit_id", "human_id" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "unit_human_permissions",
            schema: "spring");
    }
}
