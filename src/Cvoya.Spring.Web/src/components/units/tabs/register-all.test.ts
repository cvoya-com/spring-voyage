import { beforeEach, describe, expect, it } from "vitest";

import {
  AGENT_TABS,
  TENANT_TABS,
  UNIT_TABS,
} from "../aggregate";
import {
  __resetTabRegistryForTesting,
  lookupTab,
  registeredTabs,
} from "./index";

describe("tabs/register-all — every v2 slot has a component", () => {
  beforeEach(() => __resetTabRegistryForTesting());

  it("registers a component for every (kind, tab) in the v2 catalog", async () => {
    // Vite caches ESM side-effect imports, so the module body only
    // executes the first time through this test file; subsequent runs
    // still see the populated registry because we re-register from
    // each tab module's top-level on re-import via `import("./register-all")`
    // — the registry allows overwrite in non-production, which also
    // covers HMR.
    await import("./register-all");

    for (const tab of UNIT_TABS) {
      expect(
        lookupTab("Unit", tab),
        `Unit.${tab} should be registered`,
      ).not.toBeNull();
    }
    for (const tab of AGENT_TABS) {
      expect(
        lookupTab("Agent", tab),
        `Agent.${tab} should be registered`,
      ).not.toBeNull();
    }
    for (const tab of TENANT_TABS) {
      expect(
        lookupTab("Tenant", tab),
        `Tenant.${tab} should be registered`,
      ).not.toBeNull();
    }

    // Sanity: registry size equals the sum of all catalog sizes.
    const expected =
      UNIT_TABS.length + AGENT_TABS.length + TENANT_TABS.length;
    expect(registeredTabs().length).toBe(expected);
  });
});
