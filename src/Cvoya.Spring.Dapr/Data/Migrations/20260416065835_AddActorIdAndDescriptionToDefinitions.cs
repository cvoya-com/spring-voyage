using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cvoya.Spring.Dapr.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddActorIdAndDescriptionToDefinitions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "actor_id",
                schema: "spring",
                table: "unit_definitions",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "actor_id",
                schema: "spring",
                table: "agent_definitions",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "description",
                schema: "spring",
                table: "agent_definitions",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "actor_id",
                schema: "spring",
                table: "unit_definitions");

            migrationBuilder.DropColumn(
                name: "actor_id",
                schema: "spring",
                table: "agent_definitions");

            migrationBuilder.DropColumn(
                name: "description",
                schema: "spring",
                table: "agent_definitions");
        }
    }
}