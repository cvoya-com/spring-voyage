using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cvoya.Spring.Dapr.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropOrchestrationStrategyAndLabelRouting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE IF EXISTS spring.unit_policies
                    DROP COLUMN IF EXISTS label_routing;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The orchestration strategy setting was stored in
            // unit_definitions.definition JSON, not in a dedicated EF table.
            migrationBuilder.Sql(
                """
                ALTER TABLE IF EXISTS spring.unit_policies
                    ADD COLUMN IF NOT EXISTS label_routing jsonb;
                """);
        }
    }
}