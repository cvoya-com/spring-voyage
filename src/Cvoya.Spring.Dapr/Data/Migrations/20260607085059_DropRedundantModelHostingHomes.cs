// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

#nullable disable

namespace Cvoya.Spring.Dapr.Data.Migrations;

using Microsoft.EntityFrameworkCore.Migrations;

/// <summary>
/// ADR-0067 §2 (#3111) — step 2 of 2: drop the now-redundant copies of
/// unit/agent <c>model</c> / <c>hosting</c> after the
/// <c>BackfillModelHostingSingleHome</c> migration made each subject's
/// single home authoritative. Runs strictly after the backfill (migration
/// chain order) and after the reader change shipped, so nothing reads the
/// dropped copies.
/// </summary>
/// <remarks>
/// <para>
/// <b>Agents:</b> drop the <c>agent_live_config.model</c> column. The agent's
/// model single home is the jsonb <c>execution.model{provider,id}</c> (the
/// dispatch source); the dropped column was a flat, provider-less id the
/// dispatcher never read.
/// </para>
/// <para>
/// <b>Units:</b> strip the redundant <c>model</c> and <c>hosting</c> keys from
/// existing <c>unit_definitions.definition.execution</c> jsonb so "removed
/// from the unit jsonb" is literally true (ADR-0067 §2). The unit's model
/// home is <c>unit_live_config.{provider,model}</c> (backfilled in step 1) and
/// hosting always lived on <c>unit_live_config.hosting</c>. This is a data
/// migration on the <c>definition</c> column; <c>runtime</c> / <c>image</c> /
/// <c>system_prompt_mode</c> are preserved.
/// </para>
/// </remarks>
public partial class DropRedundantModelHostingHomes : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Units: strip the redundant execution.model / execution.hosting keys
        // from the unit jsonb (backfilled into unit_live_config in step 1).
        // `#-` removes the key path; only rows whose execution object is an
        // object are touched (the WHERE guard avoids rewriting unrelated rows).
        migrationBuilder.Sql(
            """
            UPDATE spring.unit_definitions
            SET definition = (definition #- '{execution,model}') #- '{execution,hosting}'
            WHERE jsonb_typeof(definition -> 'execution') = 'object'
              AND (definition #> '{execution,model}' IS NOT NULL
                   OR definition #> '{execution,hosting}' IS NOT NULL);
            """);

        // Agents: drop the redundant flat model column (jsonb is the home).
        migrationBuilder.DropColumn(
            name: "model",
            schema: "spring",
            table: "agent_live_config");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Re-add the agent column (empty). The pre-drop flat values are not
        // restored — they were dispatch-inert and are gone; a down is a
        // dev-only convenience, not a data-recovery path.
        migrationBuilder.AddColumn<string>(
            name: "model",
            schema: "spring",
            table: "agent_live_config",
            type: "character varying(256)",
            maxLength: 256,
            nullable: true);

        // Restore the unit jsonb model/hosting keys from their single home
        // (unit_live_config) so a down leaves the jsonb shape the readers
        // expected pre-#3111. Hosting is a flat string; model is the
        // structured {provider, id} object lifted from the flat columns.
        migrationBuilder.Sql(
            """
            UPDATE spring.unit_definitions ud
            SET definition = jsonb_set(
                    jsonb_set(
                        ud.definition,
                        '{execution,model}',
                        jsonb_build_object('provider', lc.provider, 'id', lc.model),
                        true),
                    '{execution,hosting}',
                    to_jsonb(lc.hosting),
                    true)
            FROM spring.unit_live_config lc
            WHERE lc.unit_id = ud.id
              AND jsonb_typeof(ud.definition -> 'execution') = 'object'
              AND lc.provider IS NOT NULL
              AND lc.model IS NOT NULL
              AND lc.hosting IS NOT NULL;
            """);
    }
}
