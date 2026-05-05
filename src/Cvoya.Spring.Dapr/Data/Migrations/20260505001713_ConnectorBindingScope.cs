using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cvoya.Spring.Dapr.Data.Migrations
{
    /// <inheritdoc />
    public partial class ConnectorBindingScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // #1671: replace the composite PK (tenant_id, connector_id) with
            // a synthetic id PK so the same (tenant, slug) pair can carry
            // separate rows for tenant-level / package-scope / unit-scope
            // bindings. Existing tenant-level rows get a freshly minted Guid
            // via gen_random_uuid() so the new PK is populated before the
            // constraint is added.
            migrationBuilder.DropPrimaryKey(
                name: "PK_tenant_connector_installs",
                schema: "spring",
                table: "tenant_connector_installs");

            migrationBuilder.AddColumn<Guid>(
                name: "id",
                schema: "spring",
                table: "tenant_connector_installs",
                type: "uuid",
                nullable: false,
                defaultValueSql: "gen_random_uuid()");

            migrationBuilder.AddColumn<Guid>(
                name: "package_install_id",
                schema: "spring",
                table: "tenant_connector_installs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "unit_id",
                schema: "spring",
                table: "tenant_connector_installs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddPrimaryKey(
                name: "PK_tenant_connector_installs",
                schema: "spring",
                table: "tenant_connector_installs",
                column: "id");

            migrationBuilder.CreateIndex(
                name: "ix_tenant_connector_installs_pkg_scope",
                schema: "spring",
                table: "tenant_connector_installs",
                columns: new[] { "tenant_id", "connector_id", "package_install_id" },
                unique: true,
                filter: "\"package_install_id\" IS NOT NULL AND \"unit_id\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_tenant_connector_installs_tenant_slug",
                schema: "spring",
                table: "tenant_connector_installs",
                columns: new[] { "tenant_id", "connector_id" },
                unique: true,
                filter: "\"package_install_id\" IS NULL AND \"unit_id\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_tenant_connector_installs_unit_scope",
                schema: "spring",
                table: "tenant_connector_installs",
                columns: new[] { "tenant_id", "connector_id", "unit_id" },
                unique: true,
                filter: "\"unit_id\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_tenant_connector_installs",
                schema: "spring",
                table: "tenant_connector_installs");

            migrationBuilder.DropIndex(
                name: "ix_tenant_connector_installs_pkg_scope",
                schema: "spring",
                table: "tenant_connector_installs");

            migrationBuilder.DropIndex(
                name: "ix_tenant_connector_installs_tenant_slug",
                schema: "spring",
                table: "tenant_connector_installs");

            migrationBuilder.DropIndex(
                name: "ix_tenant_connector_installs_unit_scope",
                schema: "spring",
                table: "tenant_connector_installs");

            migrationBuilder.DropColumn(
                name: "id",
                schema: "spring",
                table: "tenant_connector_installs");

            migrationBuilder.DropColumn(
                name: "package_install_id",
                schema: "spring",
                table: "tenant_connector_installs");

            migrationBuilder.DropColumn(
                name: "unit_id",
                schema: "spring",
                table: "tenant_connector_installs");

            migrationBuilder.AddPrimaryKey(
                name: "PK_tenant_connector_installs",
                schema: "spring",
                table: "tenant_connector_installs",
                columns: new[] { "tenant_id", "connector_id" });
        }
    }
}