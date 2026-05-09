namespace Cvoya.Spring.Dapr.Data.Migrations;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

/// <inheritdoc />
public partial class DropOrchestrationStrategyAndLabelRouting : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Orchestration strategy was stored in unit_definitions.definition
        // JSON, not in a dedicated EF-managed table.
        migrationBuilder.Sql(
            """
            ALTER TABLE IF EXISTS spring.unit_policies
                DROP COLUMN IF EXISTS label_routing;
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // No orchestration-strategy DDL to reverse; see Up comment.
        migrationBuilder.Sql(
            """
            ALTER TABLE IF EXISTS spring.unit_policies
                ADD COLUMN IF NOT EXISTS label_routing jsonb;
            """);
    }
}
