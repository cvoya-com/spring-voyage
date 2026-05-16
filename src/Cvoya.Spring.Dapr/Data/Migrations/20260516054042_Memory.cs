// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

#nullable disable

namespace Cvoya.Spring.Dapr.Data.Migrations;

using System;

using Microsoft.EntityFrameworkCore.Migrations;

/// <summary>
/// Adds the <c>memories</c> table that backs the <c>sv.memory_*</c>
/// platform tools introduced in #2342. One row per entry (long-term or
/// short-term); owner-scoped on
/// <c>(tenant_id, owner_scheme, owner_id)</c> per ADR-0036. A Postgres
/// <c>GIN(to_tsvector('english', content))</c> functional index is
/// created on <c>memories.content</c> so <c>sv.memory_search</c>'s
/// full-text path is index-backed in production. EF cannot model the
/// functional index directly, so it is emitted via raw SQL here.
/// </summary>
/// <remarks>
/// <para>
/// Topics were intentionally dropped from this migration. They will
/// return later as a *type* of memory once the memory model evolves to
/// a graph (per the #2342 follow-up review); modelling them as a
/// separate table now would force a more disruptive schema change at
/// that point.
/// </para>
/// </remarks>
public partial class Memory : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "memories",
            schema: "spring",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                owner_scheme = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                kind = table.Column<int>(type: "integer", nullable: false),
                thread_id = table.Column<Guid>(type: "uuid", nullable: true),
                content = table.Column<string>(type: "text", nullable: false),
                source = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_memories", x => x.id);
            });

        migrationBuilder.CreateIndex(
            name: "ix_memories_tenant_owner_kind_created",
            schema: "spring",
            table: "memories",
            columns: new[] { "tenant_id", "owner_scheme", "owner_id", "kind", "created_at" });

        migrationBuilder.CreateIndex(
            name: "ix_memories_tenant_owner_thread",
            schema: "spring",
            table: "memories",
            columns: new[] { "tenant_id", "owner_scheme", "owner_id", "thread_id" });

        // GIN functional index for full-text search on memory content.
        // EF Core's relational model has no first-class representation
        // for a functional index — emit the DDL directly. The Npgsql
        // FTS support in <c>EfMemoryStore.SearchAsync</c> rewrites
        // <c>EF.Functions.ToTsVector("english", content).Matches(...)</c>
        // to the same expression so the planner can use this index.
        migrationBuilder.Sql(
            "CREATE INDEX ix_memories_content_fts " +
            "ON spring.memories USING GIN (to_tsvector('english', content));");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP INDEX IF EXISTS spring.ix_memories_content_fts;");

        migrationBuilder.DropTable(
            name: "memories",
            schema: "spring");
    }
}
