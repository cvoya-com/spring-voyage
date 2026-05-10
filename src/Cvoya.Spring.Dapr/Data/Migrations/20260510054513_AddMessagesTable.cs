// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Migrations;

using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

/// <inheritdoc />
public partial class AddMessagesTable : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "messages",
            schema: "spring",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                thread_id = table.Column<Guid>(type: "uuid", nullable: false),
                sender_scheme = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                sender_id = table.Column<Guid>(type: "uuid", nullable: false),
                recipient_scheme = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                recipient_id = table.Column<Guid>(type: "uuid", nullable: false),
                message_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                body = table.Column<string>(type: "text", nullable: true),
                payload = table.Column<string>(type: "jsonb", nullable: false),
                sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                retracted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_messages", x => x.id);
                table.ForeignKey(
                    name: "fk_messages_thread_id",
                    column: x => x.thread_id,
                    principalSchema: "spring",
                    principalTable: "threads",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "ix_messages_tenant_thread_sent_at",
            schema: "spring",
            table: "messages",
            columns: new[] { "tenant_id", "thread_id", "sent_at" });

        migrationBuilder.CreateIndex(
            name: "IX_messages_thread_id",
            schema: "spring",
            table: "messages",
            column: "thread_id");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "messages",
            schema: "spring");
    }
}
