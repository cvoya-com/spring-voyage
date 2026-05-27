// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

#nullable disable

namespace Cvoya.Spring.Dapr.Data.Migrations;

using System;
using System.Text.Json;

using Microsoft.EntityFrameworkCore.Migrations;

/// <summary>
/// ADR-0061 §1 — per-tenant connector bindings.
///
/// <para>
/// Adds the <c>tenant_connector_bindings</c> table alongside the
/// existing <c>unit_connector_bindings</c> table. One row per
/// <c>(tenant_id, connector_slug)</c>; carries the connector type id,
/// typed config (opaque <c>jsonb</c>), and connector-owned runtime
/// metadata. First consumer is the Slack connector (the natural
/// unit of Slack identity is the workspace, not the SV unit); future
/// workspace-shaped connectors (calendar, shared mailbox) reuse the
/// same table per ADR-0061 §7.7.
/// </para>
/// </summary>
public partial class TenantConnectorBindings : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "tenant_connector_bindings",
            schema: "spring",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                connector_slug = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                connector_type = table.Column<Guid>(type: "uuid", nullable: false),
                config = table.Column<JsonElement>(type: "jsonb", nullable: false),
                metadata = table.Column<JsonElement>(type: "jsonb", nullable: true),
                bound_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_tenant_connector_bindings", x => x.id);
            });

        migrationBuilder.CreateIndex(
            name: "ux_tenant_connector_bindings_tenant_slug",
            schema: "spring",
            table: "tenant_connector_bindings",
            columns: new[] { "tenant_id", "connector_slug" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "tenant_connector_bindings",
            schema: "spring");
    }
}
