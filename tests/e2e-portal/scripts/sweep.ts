/**
 * Orphan-cleanup script for the portal e2e suite.
 *
 * Deletes every unit, agent, tenant secret, and API token whose name starts
 * with the configured prefix (`E2E_PORTAL_PREFIX`, default `e2e-portal`).
 *
 * Mirrors `tests/e2e/run.sh --sweep`.
 *
 * Usage:
 *   npx tsx scripts/sweep.ts
 *   E2E_PORTAL_PREFIX=e2e-portal-ci npx tsx scripts/sweep.ts
 *
 * The script never runs implicitly. The auto-cleanup hook in fixtures/test.ts
 * deletes per-test artefacts; sweep is the backstop when a test crashes
 * before the hook can fire (kill -9, network blip, machine sleep mid-run).
 */

import {
  deleteAgent,
  deleteTenantSecret,
  deleteUnit,
  isApiUp,
  listOwnedAgents,
  listOwnedTenantSecrets,
  listOwnedTokens,
  listOwnedUnits,
  revokeToken,
} from "../fixtures/api.js";
import { PREFIX } from "../fixtures/ids.js";

async function main() {
  if (!(await isApiUp())) {
    console.error(
      "API not reachable — start the local stack and retry. Sweep aborted.",
    );
    process.exit(2);
  }

  const summary = { units: 0, agents: 0, secrets: 0, tokens: 0, errors: 0 };
  const failures: string[] = [];

  console.log(`Sweeping artefacts with prefix: ${PREFIX}-*`);

  for (const a of await listOwnedAgents(PREFIX)) {
    try {
      await deleteAgent(a.id);
      summary.agents++;
    } catch (e) {
      summary.errors++;
      failures.push(`agent ${a.id}: ${String(e)}`);
    }
  }
  for (const u of await listOwnedUnits(PREFIX)) {
    try {
      await deleteUnit(u.name, true);
      summary.units++;
    } catch (e) {
      summary.errors++;
      failures.push(`unit ${u.name}: ${String(e)}`);
    }
  }
  for (const s of await listOwnedTenantSecrets(PREFIX)) {
    try {
      await deleteTenantSecret(s.name);
      summary.secrets++;
    } catch (e) {
      summary.errors++;
      failures.push(`secret ${s.name}: ${String(e)}`);
    }
  }
  for (const t of await listOwnedTokens(PREFIX)) {
    try {
      await revokeToken(t.name);
      summary.tokens++;
    } catch (e) {
      summary.errors++;
      failures.push(`token ${t.name}: ${String(e)}`);
    }
  }

  console.log(
    `Done. Deleted: ${summary.units} units, ${summary.agents} agents, ` +
      `${summary.secrets} tenant secrets, ${summary.tokens} tokens. ` +
      `Errors: ${summary.errors}.`,
  );
  if (failures.length) {
    for (const f of failures) console.error(`  - ${f}`);
    process.exit(1);
  }
}

main().catch((err) => {
  console.error(err);
  process.exit(1);
});
