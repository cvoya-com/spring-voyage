// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

#nullable disable

namespace Cvoya.Spring.Dapr.Data.Migrations;

using System;

using Microsoft.EntityFrameworkCore.Migrations;

/// <summary>
/// ADR-0061 §7.5 — Slack workspace map.
///
/// <para>
/// Adds the <c>tenant_slack_workspace_map</c> table — one row per
/// installed Slack workspace, holding the <c>team_id ↔ tenant_id</c>
/// mapping the OAuth callback and inbound webhook handler use to
/// resolve a Slack delivery to an SV tenant. The table is structured
/// for many-row growth (cloud multi-tenant) while OSS only ever
/// carries one row.
/// </para>
/// </summary>
public partial class SlackWorkspaceMap : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "tenant_slack_workspace_map",
            schema: "spring",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                team_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                team_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_tenant_slack_workspace_map", x => x.id);
            });

        migrationBuilder.CreateIndex(
            name: "ix_tenant_slack_workspace_map_tenant_id",
            schema: "spring",
            table: "tenant_slack_workspace_map",
            column: "tenant_id");

        migrationBuilder.CreateIndex(
            name: "ux_tenant_slack_workspace_map_team_id",
            schema: "spring",
            table: "tenant_slack_workspace_map",
            column: "team_id",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "tenant_slack_workspace_map",
            schema: "spring");
    }
}
