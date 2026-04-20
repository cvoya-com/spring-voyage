// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cvoya.Spring.Dapr.Data.Migrations
{
    /// <summary>
    /// Phase 1.1 of the #674 tenant-scoping refactor. Adds a
    /// <c>tenant_id</c> column to every business-data table, backfills
    /// any pre-existing rows with the <c>"default"</c> tenant via the
    /// column default, and flips the column to NOT NULL in a single
    /// atomic migration. Composite primary keys on
    /// <c>unit_memberships</c> and <c>unit_policies</c> are extended to
    /// include <c>tenant_id</c> so the same (unit_id, …) tuple can
    /// repeat across tenants.
    /// </summary>
    public partial class AddTenantScoping : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_unit_policies",
                schema: "spring",
                table: "unit_policies");

            migrationBuilder.DropPrimaryKey(
                name: "PK_unit_memberships",
                schema: "spring",
                table: "unit_memberships");

            migrationBuilder.DropIndex(
                name: "ix_unit_memberships_agent_address",
                schema: "spring",
                table: "unit_memberships");

            migrationBuilder.DropIndex(
                name: "IX_unit_definitions_unit_id",
                schema: "spring",
                table: "unit_definitions");

            migrationBuilder.DropIndex(
                name: "IX_connector_definitions_connector_id",
                schema: "spring",
                table: "connector_definitions");

            migrationBuilder.DropIndex(
                name: "IX_agent_definitions_agent_id",
                schema: "spring",
                table: "agent_definitions");

            // All AddColumn calls use defaultValue: "default" so the
            // NOT NULL column is backfilled in-place for any pre-existing
            // rows on upgrade. Fresh databases emit the column from the
            // entity configuration and the DbContext's audit hook stamps
            // the current tenant on every insert; the column default only
            // matters on the first upgrade of a database that pre-dates
            // tenant scoping.

            migrationBuilder.AddColumn<string>(
                name: "tenant_id",
                schema: "spring",
                table: "unit_policies",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "default");

            migrationBuilder.AddColumn<string>(
                name: "tenant_id",
                schema: "spring",
                table: "unit_memberships",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "default");

            migrationBuilder.AddColumn<string>(
                name: "tenant_id",
                schema: "spring",
                table: "unit_definitions",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "default");

            migrationBuilder.AddColumn<string>(
                name: "tenant_id",
                schema: "spring",
                table: "connector_definitions",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "default");

            migrationBuilder.AddColumn<string>(
                name: "tenant_id",
                schema: "spring",
                table: "api_tokens",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "default");

            migrationBuilder.AddColumn<string>(
                name: "tenant_id",
                schema: "spring",
                table: "agent_definitions",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "default");

            migrationBuilder.AddColumn<string>(
                name: "tenant_id",
                schema: "spring",
                table: "activity_events",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "default");

            migrationBuilder.AddPrimaryKey(
                name: "PK_unit_policies",
                schema: "spring",
                table: "unit_policies",
                columns: new[] { "tenant_id", "unit_id" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_unit_memberships",
                schema: "spring",
                table: "unit_memberships",
                columns: new[] { "tenant_id", "unit_id", "agent_address" });

            migrationBuilder.CreateIndex(
                name: "ix_unit_memberships_tenant_agent_address",
                schema: "spring",
                table: "unit_memberships",
                columns: new[] { "tenant_id", "agent_address" });

            migrationBuilder.CreateIndex(
                name: "IX_unit_definitions_tenant_id",
                schema: "spring",
                table: "unit_definitions",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_unit_definitions_tenant_id_unit_id",
                schema: "spring",
                table: "unit_definitions",
                columns: new[] { "tenant_id", "unit_id" },
                unique: true,
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_connector_definitions_tenant_id",
                schema: "spring",
                table: "connector_definitions",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_connector_definitions_tenant_id_connector_id",
                schema: "spring",
                table: "connector_definitions",
                columns: new[] { "tenant_id", "connector_id" },
                unique: true,
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_api_tokens_tenant_id",
                schema: "spring",
                table: "api_tokens",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_agent_definitions_tenant_id",
                schema: "spring",
                table: "agent_definitions",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_agent_definitions_tenant_id_agent_id",
                schema: "spring",
                table: "agent_definitions",
                columns: new[] { "tenant_id", "agent_id" },
                unique: true,
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_activity_events_tenant_id",
                schema: "spring",
                table: "activity_events",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_unit_policies",
                schema: "spring",
                table: "unit_policies");

            migrationBuilder.DropPrimaryKey(
                name: "PK_unit_memberships",
                schema: "spring",
                table: "unit_memberships");

            migrationBuilder.DropIndex(
                name: "ix_unit_memberships_tenant_agent_address",
                schema: "spring",
                table: "unit_memberships");

            migrationBuilder.DropIndex(
                name: "IX_unit_definitions_tenant_id",
                schema: "spring",
                table: "unit_definitions");

            migrationBuilder.DropIndex(
                name: "IX_unit_definitions_tenant_id_unit_id",
                schema: "spring",
                table: "unit_definitions");

            migrationBuilder.DropIndex(
                name: "IX_connector_definitions_tenant_id",
                schema: "spring",
                table: "connector_definitions");

            migrationBuilder.DropIndex(
                name: "IX_connector_definitions_tenant_id_connector_id",
                schema: "spring",
                table: "connector_definitions");

            migrationBuilder.DropIndex(
                name: "IX_api_tokens_tenant_id",
                schema: "spring",
                table: "api_tokens");

            migrationBuilder.DropIndex(
                name: "IX_agent_definitions_tenant_id",
                schema: "spring",
                table: "agent_definitions");

            migrationBuilder.DropIndex(
                name: "IX_agent_definitions_tenant_id_agent_id",
                schema: "spring",
                table: "agent_definitions");

            migrationBuilder.DropIndex(
                name: "IX_activity_events_tenant_id",
                schema: "spring",
                table: "activity_events");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                schema: "spring",
                table: "unit_policies");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                schema: "spring",
                table: "unit_memberships");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                schema: "spring",
                table: "unit_definitions");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                schema: "spring",
                table: "connector_definitions");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                schema: "spring",
                table: "api_tokens");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                schema: "spring",
                table: "agent_definitions");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                schema: "spring",
                table: "activity_events");

            migrationBuilder.AddPrimaryKey(
                name: "PK_unit_policies",
                schema: "spring",
                table: "unit_policies",
                column: "unit_id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_unit_memberships",
                schema: "spring",
                table: "unit_memberships",
                columns: new[] { "unit_id", "agent_address" });

            migrationBuilder.CreateIndex(
                name: "ix_unit_memberships_agent_address",
                schema: "spring",
                table: "unit_memberships",
                column: "agent_address");

            migrationBuilder.CreateIndex(
                name: "IX_unit_definitions_unit_id",
                schema: "spring",
                table: "unit_definitions",
                column: "unit_id",
                unique: true,
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_connector_definitions_connector_id",
                schema: "spring",
                table: "connector_definitions",
                column: "connector_id",
                unique: true,
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_agent_definitions_agent_id",
                schema: "spring",
                table: "agent_definitions",
                column: "agent_id",
                unique: true,
                filter: "deleted_at IS NULL");
        }
    }
}