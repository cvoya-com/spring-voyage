/**
 * Custom Playwright `test` extension for the portal suite.
 *
 * Adds:
 *   - `tracker` — per-test registry of unit/agent/token/secret names that
 *     were created by the test. After each test, the auto-cleanup hook
 *     deletes everything in the tracker via direct API calls. This is the
 *     same EXIT-trap pattern the shell suite uses (`e2e::cleanup_unit`),
 *     just hooked into Playwright's `afterEach`.
 *
 *   - `apiUp` — auto-skip the test (with a clear reason) when the local
 *     stack isn't reachable. Saves the operator from a wall of obscure
 *     timeout failures when they forgot to start the stack.
 *
 *   - `ollamaUp` — same pattern for Ollama. LLM-pool specs depend on it;
 *     fast-pool specs don't.
 *
 * Importing pattern: `import { test, expect } from "@fixtures/test";`
 * across the suite. Specs never import `@playwright/test` directly so
 * the cleanup contract can't be silently bypassed.
 */

import { test as base, expect } from "@playwright/test";

import {
  deleteAgent,
  deleteTenantSecret,
  deleteUnit,
  isApiUp,
  isOllamaUp,
  revokeToken,
} from "./api.js";

export interface ArtifactTracker {
  unit(name: string): string;
  agent(id: string): string;
  token(name: string): string;
  tenantSecret(name: string): string;
}

interface PortalFixtures {
  tracker: ArtifactTracker;
  apiUp: void;
  ollamaUp: void;
}

interface PortalWorkerFixtures {
  apiAvailability: boolean;
  ollamaAvailability: boolean;
}

export const test = base.extend<PortalFixtures, PortalWorkerFixtures>({
  // Worker-scoped readiness probes: hit each backend exactly once per
  // worker rather than once per test. Cuts dozens of redundant /health
  // pings on a multi-spec run.
  apiAvailability: [
    async ({}, use) => {
      const up = await isApiUp();
      await use(up);
    },
    { scope: "worker" },
  ],
  ollamaAvailability: [
    async ({}, use) => {
      const up = await isOllamaUp();
      await use(up);
    },
    { scope: "worker" },
  ],

  // Test-scoped guards: throw `test.skip()` rather than fail when the
  // dependency isn't available. Clear opt-in failure mode.
  apiUp: [
    async ({ apiAvailability }, use) => {
      if (!apiAvailability) {
        test.skip(
          true,
          "Spring API is not reachable. Start the local stack (deployment/spring-voyage-host.sh up) and retry.",
        );
      }
      await use();
    },
    { auto: true },
  ],
  ollamaUp: async ({ ollamaAvailability }, use) => {
    if (!ollamaAvailability) {
      test.skip(
        true,
        "Ollama is not reachable. Start it (`ollama serve`) or set LLM_BASE_URL.",
      );
    }
    await use();
  },

  // Per-test artefact registry + auto-cleanup.
  tracker: async ({}, use, testInfo) => {
    const units = new Set<string>();
    const agents = new Set<string>();
    const tokens = new Set<string>();
    const secrets = new Set<string>();

    const tracker: ArtifactTracker = {
      unit(name) {
        units.add(name);
        return name;
      },
      agent(id) {
        agents.add(id);
        return id;
      },
      token(name) {
        tokens.add(name);
        return name;
      },
      tenantSecret(name) {
        secrets.add(name);
        return name;
      },
    };

    await use(tracker);

    // Cleanup order matters:
    //   1. Agents — independent of units; deleting a unit cascades through
    //      memberships but does NOT delete the agent rows themselves.
    //   2. Units  — cascades memberships, secrets, boundary, orchestration.
    //   3. Tenant secrets, tokens — leaf resources.
    // Any cleanup error is logged via testInfo.attach but does not fail
    // the test — failure to clean up should not mask the test outcome
    // (mirrors `e2e::cleanup_unit`'s swallow-and-log contract).
    const errors: { kind: string; name: string; error: string }[] = [];

    for (const name of agents) {
      try {
        await deleteAgent(name);
      } catch (e) {
        errors.push({ kind: "agent", name, error: String(e) });
      }
    }
    for (const name of units) {
      try {
        await deleteUnit(name, true);
      } catch (e) {
        errors.push({ kind: "unit", name, error: String(e) });
      }
    }
    for (const name of secrets) {
      try {
        await deleteTenantSecret(name);
      } catch (e) {
        errors.push({ kind: "tenant-secret", name, error: String(e) });
      }
    }
    for (const name of tokens) {
      try {
        await revokeToken(name);
      } catch (e) {
        errors.push({ kind: "token", name, error: String(e) });
      }
    }

    if (errors.length > 0) {
      await testInfo.attach("cleanup-errors.json", {
        body: Buffer.from(JSON.stringify(errors, null, 2)),
        contentType: "application/json",
      });
    }
  },
});

export { expect };
