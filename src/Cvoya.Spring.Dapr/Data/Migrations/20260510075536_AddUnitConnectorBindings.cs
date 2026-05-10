// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

#nullable disable

namespace Cvoya.Spring.Dapr.Data.Migrations;

using System;
using System.Text.Json;

using Microsoft.EntityFrameworkCore.Migrations;

/// <inheritdoc />
public partial class AddUnitConnectorBindings : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "unit_connector_bindings",
            schema: "spring",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                connector_type = table.Column<Guid>(type: "uuid", nullable: false),
                config = table.Column<JsonElement>(type: "jsonb", nullable: false),
                metadata = table.Column<JsonElement>(type: "jsonb", nullable: true),
                bound_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_unit_connector_bindings", x => x.id);
            });

        migrationBuilder.CreateIndex(
            name: "ix_unit_connector_bindings_tenant_type",
            schema: "spring",
            table: "unit_connector_bindings",
            columns: new[] { "tenant_id", "connector_type" });

        migrationBuilder.CreateIndex(
            name: "ux_unit_connector_bindings_tenant_unit",
            schema: "spring",
            table: "unit_connector_bindings",
            columns: new[] { "tenant_id", "unit_id" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "unit_connector_bindings",
            schema: "spring");
    }
}
