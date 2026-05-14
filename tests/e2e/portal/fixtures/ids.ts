/**
 * Run-identity helpers — mirror `tests/e2e/_lib.sh` (`e2e::unit_name`,
 * `e2e::agent_name`) so artefacts created by this suite are sweepable by
 * the same `--sweep` mechanism the shell suite ships with, AND so two
 * concurrent invocations (one shell, one portal) never collide on the
 * server's unique-name constraint.
 *
 * Conventions
 * -----------
 * Names are formatted as `${prefix}-${runId}-${suffix}`.
 *
 *   - `prefix` is `E2E_PORTAL_PREFIX` (default `e2e-portal`). The shell
 *     suite uses `e2e` / `e2e-ci`; the portal suite uses a distinct prefix
 *     so a shell `--sweep` never wipes mid-flight portal artefacts and
 *     vice versa, even when both suites run concurrently.
 *
 *   - `runId` is `E2E_PORTAL_RUN_ID` (default: timestamp-pid). Stable for
 *     the duration of a single Playwright invocation; unique per process.
 *
 *   - `suffix` is the per-spec semantic name. Keep it short and lowercase;
 *     spring unit/agent names are limited to `[a-z0-9-]+`.
 *
 * Exposed as named functions instead of an object so call sites read like
 * the shell helpers (`unitName("scratch")` ↔ `e2e::unit_name scratch`).
 */

const DEFAULT_PREFIX = "e2e-portal";

export const PREFIX: string =
  process.env.E2E_PORTAL_PREFIX?.trim() || DEFAULT_PREFIX;

/** Stable, process-scoped run id. Computed once on module load. */
export const RUN_ID: string =
  process.env.E2E_PORTAL_RUN_ID?.trim() ||
  `${Math.floor(Date.now() / 1000)}-${process.pid}`;

function ensureUrlSafe(suffix: string): string {
  if (!/^[a-z0-9-]+$/.test(suffix)) {
    throw new Error(
      `e2e-portal: name suffix '${suffix}' must match /^[a-z0-9-]+$/ — ` +
        "the API rejects unit/agent names that don't.",
    );
  }
  return suffix;
}

export function unitName(suffix: string): string {
  return `${PREFIX}-${RUN_ID}-${ensureUrlSafe(suffix)}`;
}

export function agentName(suffix: string): string {
  return `${PREFIX}-${RUN_ID}-${ensureUrlSafe(suffix)}`;
}

export function tokenName(suffix: string): string {
  return `${PREFIX}-${RUN_ID}-tok-${ensureUrlSafe(suffix)}`;
}

export function secretName(suffix: string): string {
  // Tenant + unit secret names share the same `[a-z0-9-]+` rule as units.
  return `${PREFIX}-${RUN_ID}-sec-${ensureUrlSafe(suffix)}`;
}

/** Returns true when `name` was minted by this suite (any run id). */
export function isOwnedByPortalSuite(name: string): boolean {
  return name.startsWith(`${PREFIX}-`);
}
