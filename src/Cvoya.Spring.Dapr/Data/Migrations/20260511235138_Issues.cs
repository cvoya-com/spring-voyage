// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

#nullable disable

namespace Cvoya.Spring.Dapr.Data.Migrations;

using System;

using Microsoft.EntityFrameworkCore.Migrations;

/// <inheritdoc />
public partial class Issues : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Issues",
            schema: "spring",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                SubjectKind = table.Column<int>(type: "integer", nullable: false),
                SubjectId = table.Column<Guid>(type: "uuid", nullable: false),
                Severity = table.Column<int>(type: "integer", nullable: false),
                Source = table.Column<string>(type: "text", nullable: false),
                Code = table.Column<string>(type: "text", nullable: false),
                Title = table.Column<string>(type: "text", nullable: false),
                Detail = table.Column<string>(type: "text", nullable: true),
                TraceId = table.Column<string>(type: "text", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ClearedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Issues", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Issues_TenantId_SubjectKind_SubjectId_ClearedAt",
            schema: "spring",
            table: "Issues",
            columns: new[] { "TenantId", "SubjectKind", "SubjectId", "ClearedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_Issues_TenantId_SubjectKind_SubjectId_Source_Code",
            schema: "spring",
            table: "Issues",
            columns: new[] { "TenantId", "SubjectKind", "SubjectId", "Source", "Code" },
            unique: true,
            filter: "\"ClearedAt\" IS NULL");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "Issues",
            schema: "spring");
    }
}
