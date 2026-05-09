// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

#nullable disable

namespace Cvoya.Spring.Dapr.Data.Migrations;

using System;

using Microsoft.EntityFrameworkCore.Migrations;

/// <inheritdoc />
public partial class AddBudgetLimits : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "budget_limits",
            schema: "spring",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                scope_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                scope_id = table.Column<Guid>(type: "uuid", nullable: true),
                daily_budget = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_budget_limits", x => x.id);
            });

        migrationBuilder.CreateIndex(
            name: "ix_budget_limits_tenant_scope",
            schema: "spring",
            table: "budget_limits",
            columns: new[] { "tenant_id", "scope_type", "scope_id" },
            unique: true,
            filter: "scope_id IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "ix_budget_limits_tenant_scope_null",
            schema: "spring",
            table: "budget_limits",
            columns: new[] { "tenant_id", "scope_type" },
            unique: true,
            filter: "scope_id IS NULL");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "budget_limits",
            schema: "spring");
    }
}
