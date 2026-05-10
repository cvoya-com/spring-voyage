// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

#nullable disable

namespace Cvoya.Spring.Dapr.Data.Migrations;

using System;
using System.Text.Json;

using Microsoft.EntityFrameworkCore.Migrations;

/// <inheritdoc />
public partial class AddCloningPolicies : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "cloning_policies",
            schema: "spring",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                scope_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                scope_id = table.Column<Guid>(type: "uuid", nullable: true),
                policy = table.Column<JsonElement>(type: "jsonb", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_cloning_policies", x => x.id);
            });

        migrationBuilder.CreateIndex(
            name: "ix_cloning_policies_tenant_scope",
            schema: "spring",
            table: "cloning_policies",
            columns: new[] { "tenant_id", "scope_type", "scope_id" },
            unique: true,
            filter: "scope_id IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "ix_cloning_policies_tenant_scope_null",
            schema: "spring",
            table: "cloning_policies",
            columns: new[] { "tenant_id", "scope_type" },
            unique: true,
            filter: "scope_id IS NULL");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "cloning_policies",
            schema: "spring");
    }
}
