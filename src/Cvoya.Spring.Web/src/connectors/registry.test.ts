import { describe, expect, it } from "vitest";

import {
  getConnectorComponent,
  getConnectorWizardStep,
  getRegisteredConnectorSlugs,
} from "./registry";

// These tests are intentionally structural — they pin the invariants
// the rest of the wizard code relies on:
//   1. Every registered slug has a Connector-tab component.
//   2. A slug can optionally have a wizard-step component.
//   3. Unknown slugs return undefined from both lookups.
//
// They double as the first smoke test for #199's registry extension —
// if we add a second connector entry point (or a second connector
// package) and this file still compiles, the contract held.

describe("connector registry", () => {
  it("registers at least the GitHub and web-search connectors", () => {
    const slugs = getRegisteredConnectorSlugs();
    expect(slugs).toContain("github");
    expect(slugs).toContain("web-search");
  });

  it("returns the Connector-tab component for every registered slug", () => {
    for (const slug of getRegisteredConnectorSlugs()) {
      const tab = getConnectorComponent(slug);
      expect(tab, `tab component for slug '${slug}'`).toBeDefined();
    }
  });

  it("returns the wizard-step component for the GitHub connector", () => {
    const wizardStep = getConnectorWizardStep("github");
    expect(wizardStep).toBeDefined();
  });

  it("returns the wizard-step component for the web-search connector", () => {
    const wizardStep = getConnectorWizardStep("web-search");
    expect(wizardStep).toBeDefined();
  });

  it("returns undefined for an unknown slug on both lookups", () => {
    expect(getConnectorComponent("no-such-connector")).toBeUndefined();
    expect(getConnectorWizardStep("no-such-connector")).toBeUndefined();
  });
});
