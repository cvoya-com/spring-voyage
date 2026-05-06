using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cvoya.Spring.Dapr.Data.Migrations
{
    /// <inheritdoc />
    public partial class AgentScopeAndSecretPropagation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // #1737: per-secret propagation flag on the registry. When
            // false on a unit-scoped secret, the resolver does NOT
            // inherit the value down to child units / agents during the
            // parent-unit chain walk. The column defaults to true so
            // existing rows preserve pre-#1737 fall-through behaviour.
            // The Agent enum value (SecretScope = 3) needs no schema
            // change on its own — `scope` is already an int column.
            migrationBuilder.AddColumn<bool>(
                name: "propagate",
                schema: "spring",
                table: "secret_registry_entries",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "propagate",
                schema: "spring",
                table: "secret_registry_entries");
        }
    }
}