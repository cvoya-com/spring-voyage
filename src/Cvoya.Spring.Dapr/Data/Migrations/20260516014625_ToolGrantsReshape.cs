// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

#nullable disable

namespace Cvoya.Spring.Dapr.Data.Migrations;

using System;

using Microsoft.EntityFrameworkCore.Migrations;

/// <summary>
/// Reshapes the agent skill-grants surface into the (namespace, tool_name,
/// provenance) tool-grants tables introduced by #2335 Sub B, and adds the
/// <c>tool_namespace</c> column to <c>connector_definitions</c>. Carries
/// every existing <c>agent_skill_grants</c> row forward as an explicit
/// grant on <c>agent_tool_grants</c>; the (post-Sub-A) canonical
/// <c>&lt;namespace&gt;.&lt;tool_name&gt;</c> form lets us split the
/// namespace cleanly from the tool id at the SQL layer.
/// </summary>
public partial class ToolGrantsReshape : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // 1) connector_definitions.tool_namespace — defaults to `type`
        //    for every existing row so the auto-grant pipeline sees a
        //    populated value even before Sub A's renames propagate to
        //    every connector_definition row. New writes inherit this
        //    behaviour through SpringDbContext.ApplyAuditTimestamps.
        migrationBuilder.AddColumn<string>(
            name: "tool_namespace",
            schema: "spring",
            table: "connector_definitions",
            type: "character varying(64)",
            maxLength: 64,
            nullable: false,
            defaultValue: string.Empty);

        migrationBuilder.Sql(
            "UPDATE spring.connector_definitions " +
            "SET tool_namespace = type " +
            "WHERE tool_namespace = '' OR tool_namespace IS NULL;");

        // 2) Create the new tool-grants tables.
        migrationBuilder.CreateTable(
            name: "agent_tool_grants",
            schema: "spring",
            columns: table => new
            {
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                tool_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                provenance = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                @namespace = table.Column<string>(name: "namespace", type: "character varying(64)", maxLength: 64, nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_agent_tool_grants", x => new { x.tenant_id, x.agent_id, x.tool_name, x.provenance });
            });

        migrationBuilder.CreateTable(
            name: "unit_tool_grants",
            schema: "spring",
            columns: table => new
            {
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                tool_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                provenance = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                @namespace = table.Column<string>(name: "namespace", type: "character varying(64)", maxLength: 64, nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_unit_tool_grants", x => new { x.tenant_id, x.unit_id, x.tool_name, x.provenance });
            });

        // 3) Backfill — copy every row from agent_skill_grants over with
        //    provenance = "explicit". Sub A renamed every tool to the
        //    canonical <namespace>.<tool_name> form, so split_part(.., 1)
        //    is a safe parse for the namespace column. Any pre-Sub-A
        //    rows that somehow survived without a dot end up with a
        //    namespace equal to the whole string — operator-facing, not
        //    load-bearing.
        migrationBuilder.Sql(
            "INSERT INTO spring.agent_tool_grants (tenant_id, agent_id, tool_name, provenance, namespace, created_at) " +
            "SELECT tenant_id, agent_id, skill_name, 'explicit', " +
            "       CASE WHEN strpos(skill_name, '.') > 0 THEN split_part(skill_name, '.', 1) ELSE skill_name END, " +
            "       granted_at " +
            "FROM spring.agent_skill_grants " +
            "ON CONFLICT DO NOTHING;");

        // 4) Drop the legacy table.
        migrationBuilder.DropTable(
            name: "agent_skill_grants",
            schema: "spring");

        // 5) Indexes for the new tables.
        migrationBuilder.CreateIndex(
            name: "ix_agent_tool_grants_tenant_agent",
            schema: "spring",
            table: "agent_tool_grants",
            columns: new[] { "tenant_id", "agent_id" });

        migrationBuilder.CreateIndex(
            name: "ix_agent_tool_grants_tenant_agent_provenance",
            schema: "spring",
            table: "agent_tool_grants",
            columns: new[] { "tenant_id", "agent_id", "provenance" });

        migrationBuilder.CreateIndex(
            name: "ix_agent_tool_grants_tenant_namespace",
            schema: "spring",
            table: "agent_tool_grants",
            columns: new[] { "tenant_id", "namespace" });

        migrationBuilder.CreateIndex(
            name: "ix_unit_tool_grants_tenant_namespace",
            schema: "spring",
            table: "unit_tool_grants",
            columns: new[] { "tenant_id", "namespace" });

        migrationBuilder.CreateIndex(
            name: "ix_unit_tool_grants_tenant_unit",
            schema: "spring",
            table: "unit_tool_grants",
            columns: new[] { "tenant_id", "unit_id" });

        migrationBuilder.CreateIndex(
            name: "ix_unit_tool_grants_tenant_unit_provenance",
            schema: "spring",
            table: "unit_tool_grants",
            columns: new[] { "tenant_id", "unit_id", "provenance" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Re-create the legacy table before dropping the new ones so
        // a partial-rollback consumer sees the table shape it
        // originally migrated against. Backfill copies only the
        // "explicit" rows back — connector / image / platform rows
        // had no row representation in the legacy world.
        migrationBuilder.CreateTable(
            name: "agent_skill_grants",
            schema: "spring",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                granted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                skill_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_agent_skill_grants", x => x.id);
            });

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

        migrationBuilder.Sql(
            "INSERT INTO spring.agent_skill_grants (id, tenant_id, agent_id, skill_name, granted_at) " +
            "SELECT gen_random_uuid(), tenant_id, agent_id, tool_name, created_at " +
            "FROM spring.agent_tool_grants " +
            "WHERE provenance = 'explicit';");

        migrationBuilder.DropTable(
            name: "agent_tool_grants",
            schema: "spring");

        migrationBuilder.DropTable(
            name: "unit_tool_grants",
            schema: "spring");

        migrationBuilder.DropColumn(
            name: "tool_namespace",
            schema: "spring",
            table: "connector_definitions");
    }
}
