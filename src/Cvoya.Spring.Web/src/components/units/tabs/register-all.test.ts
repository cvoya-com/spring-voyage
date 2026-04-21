import { beforeEach, describe, expect, it } from "vitest";

import { tabsFor } from "../aggregate";
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

    const unitTabs = tabsFor("Unit");
    const agentTabs = tabsFor("Agent");
    const tenantTabs = tabsFor("Tenant");

    for (const tab of unitTabs) {
      expect(
        lookupTab("Unit", tab),
        `Unit.${tab} should be registered`,
      ).not.toBeNull();
    }
    for (const tab of agentTabs) {
      expect(
        lookupTab("Agent", tab),
        `Agent.${tab} should be registered`,
      ).not.toBeNull();
    }
    for (const tab of tenantTabs) {
      expect(
        lookupTab("Tenant", tab),
        `Tenant.${tab} should be registered`,
      ).not.toBeNull();
    }

    // Sanity: registry size equals the sum of all catalog sizes
    // (including overflow tabs — they're first-class registry citizens).
    const expected = unitTabs.length + agentTabs.length + tenantTabs.length;
    expect(registeredTabs().length).toBe(expected);
  });
});
