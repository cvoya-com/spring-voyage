// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

#nullable disable

namespace Cvoya.Spring.Dapr.Data.Migrations;

using Microsoft.EntityFrameworkCore.Migrations;

/// <summary>
/// Drops the redundant <c>kind</c> column from <c>spring.memories</c>
/// (#2997, ADR-0065). The memory recall scope is now <b>derived from</b>
/// <c>thread_id</c> — agent-scoped when null, thread-scoped otherwise —
/// rather than stored, so the two can never drift. The owner-scoped list
/// index loses its <c>kind</c> component (renamed
/// <c>ix_memories_tenant_owner_kind_created</c> →
/// <c>ix_memories_tenant_owner_created</c>); the
/// <c>ix_memories_tenant_owner_thread</c> index already backs both
/// thread-scoped recall and the derived scope filter. v0.1 aggressive
/// cleanup — no back-compat shim.
/// </summary>
public partial class MemoryScopeFromThreadId : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "ix_memories_tenant_owner_kind_created",
            schema: "spring",
            table: "memories");

        migrationBuilder.DropColumn(
            name: "kind",
            schema: "spring",
            table: "memories");

        migrationBuilder.CreateIndex(
            name: "ix_memories_tenant_owner_created",
            schema: "spring",
            table: "memories",
            columns: new[] { "tenant_id", "owner_scheme", "owner_id", "created_at" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "ix_memories_tenant_owner_created",
            schema: "spring",
            table: "memories");

        migrationBuilder.AddColumn<int>(
            name: "kind",
            schema: "spring",
            table: "memories",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.CreateIndex(
            name: "ix_memories_tenant_owner_kind_created",
            schema: "spring",
            table: "memories",
            columns: new[] { "tenant_id", "owner_scheme", "owner_id", "kind", "created_at" });
    }
}
