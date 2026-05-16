// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

#nullable disable

namespace Cvoya.Spring.Dapr.Data.Migrations;

using System;

using Microsoft.EntityFrameworkCore.Migrations;

/// <summary>
/// Adds the three memory tables that back the <c>sv.memory_*</c> /
/// <c>sv.topic_*</c> platform tools introduced in #2342: <c>memories</c>
/// (one row per entry), <c>memory_topics</c> (owner-unique topic names),
/// and <c>memory_topic_links</c> (the junction). A Postgres
/// <c>GIN(to_tsvector('english', content))</c> functional index is
/// created on <c>memories.content</c> so <c>sv.memory_search</c>'s
/// full-text path is index-backed in production. EF cannot model the
/// functional index directly, so it is emitted via raw SQL here.
/// </summary>
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

        migrationBuilder.CreateTable(
            name: "memory_topic_links",
            schema: "spring",
            columns: table => new
            {
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                memory_id = table.Column<Guid>(type: "uuid", nullable: false),
                topic_id = table.Column<Guid>(type: "uuid", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_memory_topic_links", x => new { x.tenant_id, x.memory_id, x.topic_id });
            });

        migrationBuilder.CreateTable(
            name: "memory_topics",
            schema: "spring",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                owner_scheme = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                description = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_memory_topics", x => x.id);
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

        migrationBuilder.CreateIndex(
            name: "ix_memory_topic_links_tenant_memory",
            schema: "spring",
            table: "memory_topic_links",
            columns: new[] { "tenant_id", "memory_id" });

        migrationBuilder.CreateIndex(
            name: "ix_memory_topic_links_tenant_topic",
            schema: "spring",
            table: "memory_topic_links",
            columns: new[] { "tenant_id", "topic_id" });

        migrationBuilder.CreateIndex(
            name: "ux_memory_topics_tenant_owner_name",
            schema: "spring",
            table: "memory_topics",
            columns: new[] { "tenant_id", "owner_scheme", "owner_id", "name" },
            unique: true);

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

        migrationBuilder.DropTable(
            name: "memory_topic_links",
            schema: "spring");

        migrationBuilder.DropTable(
            name: "memory_topics",
            schema: "spring");
    }
}
