using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cvoya.Spring.Dapr.Data.Migrations
{
    /// <summary>
    /// ADR-0067 §2 (#3111) — step 1 of 2: <b>backfill</b> the chosen single home
    /// for unit/agent <c>model</c> from the (about-to-be-redundant) copy, where
    /// the home is unset. This migration is data-only (no schema change) and
    /// MUST land before the reader change is relied on and before the
    /// <c>DropRedundantModelHostingHomes</c> migration strips the redundant
    /// copies, so a unit never loses its model/hosting mid-migration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Units</b> are single-homed on <c>unit_live_config.{provider,model}</c>.
    /// Lift the unit jsonb <c>execution.model{provider,id}</c> onto
    /// <c>unit_live_config.{provider,model}</c> wherever the live-config pair is
    /// unset (UPDATE existing rows; INSERT a row for units that carry a jsonb
    /// model but have no live-config row yet). Unit <c>hosting</c> already lived
    /// only on <c>unit_live_config</c>, so there is no hosting backfill.
    /// </para>
    /// <para>
    /// <b>Agents</b> are single-homed on the jsonb <c>execution.model{provider,id}</c>
    /// — already the dispatch source. <c>agent_live_config.model</c> is a flat,
    /// provider-less id that the dispatcher never read; it is dropped in step 2.
    /// There is intentionally no flat-to-jsonb fold here: the canonical jsonb
    /// model requires a <c>provider</c>, which a flat-only value cannot supply,
    /// and folding an invalid provider-less model would corrupt the dispatch
    /// home. Because the flat value was dispatch-inert, dropping it changes no
    /// dispatch behaviour; the only observable effect is that an agent whose
    /// model lived <i>only</i> in the flat column (no jsonb model) surfaces a
    /// null <c>AgentMetadata.Model</c> afterwards (GET response / model-policy
    /// input) — the model was never applied at dispatch regardless. In practice
    /// <c>agent_live_config.model</c> is populated only by the removed flat
    /// metadata-PATCH writer, so this is a no-op on real data.
    /// </para>
    /// </remarks>
    public partial class BackfillModelHostingSingleHome : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Units: UPDATE existing unit_live_config rows where the model home
            // is unset, lifting the jsonb execution.model{provider,id}.
            migrationBuilder.Sql(
                """
                UPDATE spring.unit_live_config lc
                SET provider = COALESCE(lc.provider, ud.definition #>> '{execution,model,provider}'),
                    model    = COALESCE(lc.model,    ud.definition #>> '{execution,model,id}'),
                    updated_at = now()
                FROM spring.unit_definitions ud
                WHERE ud.id = lc.unit_id
                  AND ud.deleted_at IS NULL
                  AND (lc.provider IS NULL OR lc.model IS NULL)
                  AND ud.definition #>> '{execution,model,provider}' IS NOT NULL
                  AND ud.definition #>> '{execution,model,id}' IS NOT NULL;
                """);

            // Units: INSERT a unit_live_config row for units that carry a jsonb
            // model but have no live-config row at all. Non-defaulted required
            // columns (tenant_id, execution_mode, permission_inheritance,
            // updated_at) are supplied explicitly; the DB defaults cover
            // enabled / expertise_initialised / lifecycle_status.
            migrationBuilder.Sql(
                """
                INSERT INTO spring.unit_live_config
                    (unit_id, tenant_id, provider, model, execution_mode, permission_inheritance, updated_at)
                SELECT ud.id,
                       ud.tenant_id,
                       ud.definition #>> '{execution,model,provider}',
                       ud.definition #>> '{execution,model,id}',
                       0,
                       0,
                       now()
                FROM spring.unit_definitions ud
                WHERE ud.deleted_at IS NULL
                  AND ud.definition #>> '{execution,model,provider}' IS NOT NULL
                  AND ud.definition #>> '{execution,model,id}' IS NOT NULL
                  AND NOT EXISTS (
                      SELECT 1 FROM spring.unit_live_config lc WHERE lc.unit_id = ud.id);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The backfill into the single home (unit_live_config) is not
            // reversible: the jsonb copy it lifted from is still present after
            // this migration (it is only stripped by step 2), so a down here
            // has nothing meaningful to restore. Leaving the backfilled values
            // in place is the safe no-op — they match the jsonb source they were
            // copied from.
        }
    }
}
