using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cvoya.Spring.Dapr.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddUnitValidationTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "last_validation_error_json",
                schema: "spring",
                table: "unit_definitions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "last_validation_run_id",
                schema: "spring",
                table: "unit_definitions",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "last_validation_error_json",
                schema: "spring",
                table: "unit_definitions");

            migrationBuilder.DropColumn(
                name: "last_validation_run_id",
                schema: "spring",
                table: "unit_definitions");
        }
    }
}