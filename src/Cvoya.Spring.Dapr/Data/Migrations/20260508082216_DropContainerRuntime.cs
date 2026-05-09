namespace Cvoya.Spring.Dapr.Data.Migrations;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

/// <inheritdoc />
public partial class DropContainerRuntime : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            ALTER TABLE IF EXISTS spring.agent_definitions
                DROP COLUMN IF EXISTS container_runtime;

            ALTER TABLE IF EXISTS spring.unit_definitions
                DROP COLUMN IF EXISTS container_runtime;
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            ALTER TABLE IF EXISTS spring.agent_definitions
                ADD COLUMN IF NOT EXISTS container_runtime character varying(32);

            ALTER TABLE IF EXISTS spring.unit_definitions
                ADD COLUMN IF NOT EXISTS container_runtime character varying(32);
            """);
    }
}
