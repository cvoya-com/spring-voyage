// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

#nullable disable

namespace Cvoya.Spring.Dapr.Data.Migrations;

using System;
using System.Text.Json;

using Microsoft.EntityFrameworkCore.Migrations;

/// <inheritdoc />
public partial class AddUnitLiveConfigAndExpertise : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "unit_expertise",
            schema: "spring",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                description = table.Column<string>(type: "text", nullable: false),
                level = table.Column<int>(type: "integer", nullable: true),
                input_schema_json = table.Column<string>(type: "text", nullable: true),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_unit_expertise", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "unit_live_config",
            schema: "spring",
            columns: table => new
            {
                unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                model = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                color = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                provider = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                hosting = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                permission_inheritance = table.Column<int>(type: "integer", nullable: false),
                boundary = table.Column<JsonElement>(type: "jsonb", nullable: true),
                expertise_initialised = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_unit_live_config", x => x.unit_id);
            });

        migrationBuilder.CreateIndex(
            name: "ix_unit_expertise_tenant_unit",
            schema: "spring",
            table: "unit_expertise",
            columns: new[] { "tenant_id", "unit_id" });

        migrationBuilder.CreateIndex(
            name: "ux_unit_expertise_tenant_unit_name",
            schema: "spring",
            table: "unit_expertise",
            columns: new[] { "tenant_id", "unit_id", "name" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_unit_live_config_tenant_id",
            schema: "spring",
            table: "unit_live_config",
            column: "tenant_id");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "unit_expertise",
            schema: "spring");

        migrationBuilder.DropTable(
            name: "unit_live_config",
            schema: "spring");
    }
}
