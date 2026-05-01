using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cvoya.Spring.Dapr.Data.Migrations
{
    /// <inheritdoc />
    public partial class MigrateUnitMembershipToUuidKeys : Migration
    {
        // Issue #1503: the original migration body shipped in #1492 went
        // straight from `(unit_id varchar, agent_address varchar)` to
        // `(unit_id uuid, agent_id uuid)` via:
        //   - AlterColumn<Guid>("unit_id", type: "uuid", oldType: "varchar(256)")
        //   - DropColumn("agent_address") + AddColumn<Guid>("agent_id",
        //     ..., NOT NULL, default '00000000-0000-0000-0000-000000000000')
        //
        // Two failure modes that crashloop the worker on any populated db:
        //   1. Postgres can't auto-cast varchar→uuid; EF emits a plain
        //      ALTER COLUMN ... TYPE uuid, no USING clause, so the migration
        //      aborts with 42804 ("column unit_id cannot be cast
        //      automatically to type uuid") before any DDL commits.
        //   2. Even with a USING clause, the existing values are slugs
        //      (e.g. 'sv-test', 'tech-lead') — they aren't parseable as
        //      uuids. AND the new agent_id column would back-populate to
        //      the zero-GUID for every row, breaking the new (tenant_id,
        //      unit_id, agent_id) PK as soon as a unit has ≥ 2 members.
        //
        // The fix: add the new uuid columns nullable, backfill them by
        // joining against unit_definitions / agent_definitions on the
        // legacy slug values, drop unresolvable rows (legacy orphans), then
        // drop the varchar columns and tighten to NOT NULL. Same end state
        // as the original migration, just data-preserving.
        //
        // Down does the symmetric translation: re-add the legacy varchar
        // columns, backfill them from the uuid columns by joining back to
        // the slug-bearing definition tables, drop the uuid columns.

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Tear down PK + secondary index so we can rebuild on the new
            // key shape once the columns are in place.
            migrationBuilder.DropPrimaryKey(
                name: "PK_unit_memberships",
                schema: "spring",
                table: "unit_memberships");

            migrationBuilder.DropIndex(
                name: "ix_unit_memberships_tenant_agent_address",
                schema: "spring",
                table: "unit_memberships");

            // Add the new uuid columns NULLABLE so we can backfill them
            // from joins before tightening to NOT NULL. `unit_id_tmp` is
            // a temporary name — we drop the legacy varchar `unit_id` and
            // rename this column over the top of it once backfilled.
            migrationBuilder.AddColumn<Guid>(
                name: "agent_id",
                schema: "spring",
                table: "unit_memberships",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "unit_id_tmp",
                schema: "spring",
                table: "unit_memberships",
                type: "uuid",
                nullable: true);

            // Backfill from the stable ActorId UUIDs on the *_definitions
            // tables. The legacy unit_memberships.unit_id stored the unit
            // slug (matches unit_definitions.unit_id, also a slug), and
            // agent_address stored the agent slug (matches
            // agent_definitions.agent_id, the slug column). The canonical
            // UUID consumers expect — and that DirectoryService surfaces
            // as DirectoryEntry.ActorId — is the *actor_id* column on
            // each definitions table, NOT the row PK `id`. Joining on
            // the row PK would give every membership a different UUID
            // than the unit/agent's externally-visible identity and
            // break every actor lookup that takes the membership row's
            // unit_id/agent_id and feeds it to a DirectoryService
            // resolution.
            //
            // Rows that fail to resolve — orphans, e2e test leftovers
            // whose agent/unit was deleted — get dropped. They would
            // violate the new NOT NULL constraint either way and are not
            // referenced by any live actor state.
            migrationBuilder.Sql(@"
                UPDATE spring.unit_memberships AS m
                SET unit_id_tmp = u.actor_id::uuid,
                    agent_id    = a.actor_id::uuid
                FROM spring.unit_definitions  AS u,
                     spring.agent_definitions AS a
                WHERE u.unit_id   = m.unit_id
                  AND u.tenant_id = m.tenant_id
                  AND a.agent_id  = m.agent_address
                  AND a.tenant_id = m.tenant_id
                  AND u.actor_id IS NOT NULL
                  AND a.actor_id IS NOT NULL;

                DELETE FROM spring.unit_memberships
                 WHERE unit_id_tmp IS NULL
                    OR agent_id    IS NULL;
            ");

            // Now that every surviving row is backfilled, drop the legacy
            // varchar columns and adopt the uuid-typed ones.
            migrationBuilder.DropColumn(
                name: "agent_address",
                schema: "spring",
                table: "unit_memberships");

            migrationBuilder.DropColumn(
                name: "unit_id",
                schema: "spring",
                table: "unit_memberships");

            migrationBuilder.RenameColumn(
                name: "unit_id_tmp",
                schema: "spring",
                table: "unit_memberships",
                newName: "unit_id");

            // Tighten to NOT NULL.
            migrationBuilder.AlterColumn<Guid>(
                name: "unit_id",
                schema: "spring",
                table: "unit_memberships",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "agent_id",
                schema: "spring",
                table: "unit_memberships",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddPrimaryKey(
                name: "PK_unit_memberships",
                schema: "spring",
                table: "unit_memberships",
                columns: new[] { "tenant_id", "unit_id", "agent_id" });

            migrationBuilder.CreateIndex(
                name: "ix_unit_memberships_tenant_agent_id",
                schema: "spring",
                table: "unit_memberships",
                columns: new[] { "tenant_id", "agent_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Symmetric reversal: re-add nullable varchar columns,
            // backfill them from the uuid columns by joining back to the
            // slug-bearing *_definitions tables, drop the uuid columns,
            // rename the new unit_id back into place, and re-tighten.
            migrationBuilder.DropPrimaryKey(
                name: "PK_unit_memberships",
                schema: "spring",
                table: "unit_memberships");

            migrationBuilder.DropIndex(
                name: "ix_unit_memberships_tenant_agent_id",
                schema: "spring",
                table: "unit_memberships");

            migrationBuilder.AddColumn<string>(
                name: "agent_address",
                schema: "spring",
                table: "unit_memberships",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "unit_id_legacy",
                schema: "spring",
                table: "unit_memberships",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.Sql(@"
                UPDATE spring.unit_memberships AS m
                SET unit_id_legacy = u.unit_id,
                    agent_address  = a.agent_id
                FROM spring.unit_definitions  AS u,
                     spring.agent_definitions AS a
                WHERE u.actor_id::uuid = m.unit_id
                  AND u.tenant_id      = m.tenant_id
                  AND a.actor_id::uuid = m.agent_id
                  AND a.tenant_id      = m.tenant_id;

                DELETE FROM spring.unit_memberships
                 WHERE unit_id_legacy IS NULL
                    OR agent_address  IS NULL;
            ");

            migrationBuilder.DropColumn(
                name: "agent_id",
                schema: "spring",
                table: "unit_memberships");

            migrationBuilder.DropColumn(
                name: "unit_id",
                schema: "spring",
                table: "unit_memberships");

            migrationBuilder.RenameColumn(
                name: "unit_id_legacy",
                schema: "spring",
                table: "unit_memberships",
                newName: "unit_id");

            migrationBuilder.AlterColumn<string>(
                name: "unit_id",
                schema: "spring",
                table: "unit_memberships",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(256)",
                oldMaxLength: 256,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "agent_address",
                schema: "spring",
                table: "unit_memberships",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(256)",
                oldMaxLength: 256,
                oldNullable: true);

            migrationBuilder.AddPrimaryKey(
                name: "PK_unit_memberships",
                schema: "spring",
                table: "unit_memberships",
                columns: new[] { "tenant_id", "unit_id", "agent_address" });

            migrationBuilder.CreateIndex(
                name: "ix_unit_memberships_tenant_agent_address",
                schema: "spring",
                table: "unit_memberships",
                columns: new[] { "tenant_id", "agent_address" });
        }
    }
}