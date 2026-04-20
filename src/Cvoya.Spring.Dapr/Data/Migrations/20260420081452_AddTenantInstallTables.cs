using System;
using System.Text.Json;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cvoya.Spring.Dapr.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantInstallTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tenant_agent_runtime_installs",
                schema: "spring",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    runtime_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    config = table.Column<JsonElement>(type: "jsonb", nullable: true),
                    installed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_agent_runtime_installs", x => new { x.tenant_id, x.runtime_id });
                });

            migrationBuilder.CreateTable(
                name: "tenant_connector_installs",
                schema: "spring",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    connector_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    config = table.Column<JsonElement>(type: "jsonb", nullable: true),
                    installed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_connector_installs", x => new { x.tenant_id, x.connector_id });
                });

            migrationBuilder.CreateIndex(
                name: "IX_tenant_agent_runtime_installs_tenant_id",
                schema: "spring",
                table: "tenant_agent_runtime_installs",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_tenant_connector_installs_tenant_id",
                schema: "spring",
                table: "tenant_connector_installs",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tenant_agent_runtime_installs",
                schema: "spring");

            migrationBuilder.DropTable(
                name: "tenant_connector_installs",
                schema: "spring");
        }
    }
}