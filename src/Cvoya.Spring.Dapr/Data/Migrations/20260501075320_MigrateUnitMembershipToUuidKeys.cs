using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cvoya.Spring.Dapr.Data.Migrations
{
    /// <inheritdoc />
    public partial class MigrateUnitMembershipToUuidKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_unit_memberships",
                schema: "spring",
                table: "unit_memberships");

            migrationBuilder.DropIndex(
                name: "ix_unit_memberships_tenant_agent_address",
                schema: "spring",
                table: "unit_memberships");

            migrationBuilder.DropColumn(
                name: "agent_address",
                schema: "spring",
                table: "unit_memberships");

            migrationBuilder.AlterColumn<Guid>(
                name: "unit_id",
                schema: "spring",
                table: "unit_memberships",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(256)",
                oldMaxLength: 256);

            migrationBuilder.AddColumn<Guid>(
                name: "agent_id",
                schema: "spring",
                table: "unit_memberships",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddPrimaryKey(
                name: "PK_unit_memberships",
                schema: "spring",
                table: "unit_memberships",
                columns: new[] { "tenant_id", "unit_id", "agent_id" });

            migrationBuilder.CreateIndex(
                name: "ix_unit_memberships_tenant_agent_id",
                schema: "spring",
                table: "unit_memberships",
                columns: new[] { "tenant_id", "agent_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_unit_memberships",
                schema: "spring",
                table: "unit_memberships");

            migrationBuilder.DropIndex(
                name: "ix_unit_memberships_tenant_agent_id",
                schema: "spring",
                table: "unit_memberships");

            migrationBuilder.DropColumn(
                name: "agent_id",
                schema: "spring",
                table: "unit_memberships");

            migrationBuilder.AlterColumn<string>(
                name: "unit_id",
                schema: "spring",
                table: "unit_memberships",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<string>(
                name: "agent_address",
                schema: "spring",
                table: "unit_memberships",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

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
        }
    }
}