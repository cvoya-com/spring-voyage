// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

#nullable disable

namespace Cvoya.Spring.Dapr.Data.Migrations;

using System;

using Microsoft.EntityFrameworkCore.Migrations;

/// <summary>
/// Adds the <c>human_connector_identities</c> table introduced in #2408
/// for the v0.1 dogfooding plan. Stores one row per <c>(tenant, connector,
/// connector_user_id)</c> tuple — the bridge between a stable human UUID
/// (used by sv.* MCP tools) and a connector-native user id (used by
/// container-native CLI tools like <c>gh</c> / <c>git</c>).
/// </summary>
/// <remarks>
/// Uniqueness is enforced by <c>ux_human_connector_identities_tenant_connector_user</c>
/// — one external identity maps to at most one human per tenant.
/// Including <c>human_id</c> in the uniqueness would weaken the
/// constraint and let two humans claim the same login (intentionally
/// rejected during design). The secondary index on
/// <c>(tenant_id, human_id)</c> backs the "list this human's identities"
/// read pattern.
/// </remarks>
public partial class HumanConnectorIdentities : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "human_connector_identities",
            schema: "spring",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                human_id = table.Column<Guid>(type: "uuid", nullable: false),
                connector_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                connector_user_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                display_handle = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_human_connector_identities", x => x.id);
            });

        migrationBuilder.CreateIndex(
            name: "ix_human_connector_identities_tenant_human",
            schema: "spring",
            table: "human_connector_identities",
            columns: new[] { "tenant_id", "human_id" });

        migrationBuilder.CreateIndex(
            name: "ux_human_connector_identities_tenant_connector_user",
            schema: "spring",
            table: "human_connector_identities",
            columns: new[] { "tenant_id", "connector_id", "connector_user_id" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "human_connector_identities",
            schema: "spring");
    }
}
