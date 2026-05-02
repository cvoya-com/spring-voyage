using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cvoya.Spring.Dapr.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPackageInstalls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "install_id",
                schema: "spring",
                table: "unit_definitions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "install_state",
                schema: "spring",
                table: "unit_definitions",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Active");

            migrationBuilder.AddColumn<Guid>(
                name: "install_id",
                schema: "spring",
                table: "tenant_skill_bundle_bindings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "install_state",
                schema: "spring",
                table: "tenant_skill_bundle_bindings",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Active");

            migrationBuilder.AddColumn<Guid>(
                name: "install_id",
                schema: "spring",
                table: "connector_definitions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "install_state",
                schema: "spring",
                table: "connector_definitions",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Active");

            migrationBuilder.CreateTable(
                name: "package_installs",
                schema: "spring",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    install_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    package_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    original_manifest_yaml = table.Column<string>(type: "text", nullable: false),
                    inputs_json = table.Column<string>(type: "text", nullable: false),
                    package_root = table.Column<string>(type: "text", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    error_message = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_package_installs", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_package_installs_tenant_id",
                schema: "spring",
                table: "package_installs",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_package_installs_tenant_id_install_id",
                schema: "spring",
                table: "package_installs",
                columns: new[] { "tenant_id", "install_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "package_installs",
                schema: "spring");

            migrationBuilder.DropColumn(
                name: "install_id",
                schema: "spring",
                table: "unit_definitions");

            migrationBuilder.DropColumn(
                name: "install_state",
                schema: "spring",
                table: "unit_definitions");

            migrationBuilder.DropColumn(
                name: "install_id",
                schema: "spring",
                table: "tenant_skill_bundle_bindings");

            migrationBuilder.DropColumn(
                name: "install_state",
                schema: "spring",
                table: "tenant_skill_bundle_bindings");

            migrationBuilder.DropColumn(
                name: "install_id",
                schema: "spring",
                table: "connector_definitions");

            migrationBuilder.DropColumn(
                name: "install_state",
                schema: "spring",
                table: "connector_definitions");
        }
    }
}