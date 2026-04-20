using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cvoya.Spring.Dapr.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCredentialHealth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "credential_health",
                schema: "spring",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    subject_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    secret_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    last_checked = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_credential_health", x => new { x.tenant_id, x.kind, x.subject_id, x.secret_name });
                });

            migrationBuilder.CreateIndex(
                name: "IX_credential_health_tenant_id",
                schema: "spring",
                table: "credential_health",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_credential_health_tenant_id_kind",
                schema: "spring",
                table: "credential_health",
                columns: new[] { "tenant_id", "kind" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "credential_health",
                schema: "spring");
        }
    }
}