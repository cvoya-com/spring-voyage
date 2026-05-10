// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

#nullable disable

namespace Cvoya.Spring.Dapr.Data.Migrations;

using System;

using Microsoft.EntityFrameworkCore.Migrations;

/// <inheritdoc />
public partial class AddAgentLiveConfig : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "agent_expertise",
            schema: "spring",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                description = table.Column<string>(type: "text", nullable: false),
                level = table.Column<int>(type: "integer", nullable: true),
                input_schema_json = table.Column<string>(type: "text", nullable: true),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_agent_expertise", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "agent_live_config",
            schema: "spring",
            columns: table => new
            {
                agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                model = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                specialty = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                execution_mode = table.Column<int>(type: "integer", nullable: false),
                expertise_initialised = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_agent_live_config", x => x.agent_id);
            });

        migrationBuilder.CreateTable(
            name: "agent_skill_grants",
            schema: "spring",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                skill_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                granted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_agent_skill_grants", x => x.id);
            });

        migrationBuilder.CreateIndex(
            name: "ix_agent_expertise_tenant_agent",
            schema: "spring",
            table: "agent_expertise",
            columns: new[] { "tenant_id", "agent_id" });

        migrationBuilder.CreateIndex(
            name: "ux_agent_expertise_tenant_agent_name",
            schema: "spring",
            table: "agent_expertise",
            columns: new[] { "tenant_id", "agent_id", "name" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_agent_live_config_tenant_id",
            schema: "spring",
            table: "agent_live_config",
            column: "tenant_id");

        migrationBuilder.CreateIndex(
            name: "ix_agent_skill_grants_tenant_agent",
            schema: "spring",
            table: "agent_skill_grants",
            columns: new[] { "tenant_id", "agent_id" });

        migrationBuilder.CreateIndex(
            name: "ux_agent_skill_grants_tenant_agent_skill",
            schema: "spring",
            table: "agent_skill_grants",
            columns: new[] { "tenant_id", "agent_id", "skill_name" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "agent_expertise",
            schema: "spring");

        migrationBuilder.DropTable(
            name: "agent_live_config",
            schema: "spring");

        migrationBuilder.DropTable(
            name: "agent_skill_grants",
            schema: "spring");
    }
}
