// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

using System.Text.Json;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cvoya.Spring.Dapr.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPolicyDimensions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<JsonElement>(
                name: "cost",
                schema: "spring",
                table: "unit_policies",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<JsonElement>(
                name: "execution_mode",
                schema: "spring",
                table: "unit_policies",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<JsonElement>(
                name: "initiative",
                schema: "spring",
                table: "unit_policies",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<JsonElement>(
                name: "model",
                schema: "spring",
                table: "unit_policies",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "cost",
                schema: "spring",
                table: "unit_policies");

            migrationBuilder.DropColumn(
                name: "execution_mode",
                schema: "spring",
                table: "unit_policies");

            migrationBuilder.DropColumn(
                name: "initiative",
                schema: "spring",
                table: "unit_policies");

            migrationBuilder.DropColumn(
                name: "model",
                schema: "spring",
                table: "unit_policies");
        }
    }
}