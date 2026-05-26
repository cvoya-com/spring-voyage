import { describe, expect, it } from "vitest";

import {
  getConnectorComponent,
  getConnectorWizardInitialValueFromDefaults,
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

  // Issue #2780: per-connector defaults projector — package-author
  // defaults from the requires: block become the wizard step's
  // initialValue so installers don't have to re-type known filters.
  describe("getConnectorWizardInitialValueFromDefaults (github)", () => {
    it("projects include labels onto the wizard initialValue", () => {
      const seed = getConnectorWizardInitialValueFromDefaults("github", {
        labels: { include: ["spring-voyage-team"], exclude: [] },
      });
      expect(seed).toEqual({ include_labels: ["spring-voyage-team"] });
    });

    it("projects exclude labels onto the wizard initialValue", () => {
      const seed = getConnectorWizardInitialValueFromDefaults("github", {
        labels: { include: [], exclude: ["wip", "internal:*"] },
      });
      expect(seed).toEqual({ exclude_labels: ["wip", "internal:*"] });
    });

    it("merges include and exclude when both present", () => {
      const seed = getConnectorWizardInitialValueFromDefaults("github", {
        labels: { include: ["team"], exclude: ["wip"] },
      });
      expect(seed).toEqual({
        include_labels: ["team"],
        exclude_labels: ["wip"],
      });
    });

    it("returns null when the defaults block carries no labels", () => {
      const seed = getConnectorWizardInitialValueFromDefaults("github", {
        labels: null,
      });
      expect(seed).toBeNull();
    });

    it("returns null when both include and exclude are empty", () => {
      const seed = getConnectorWizardInitialValueFromDefaults("github", {
        labels: { include: [], exclude: [] },
      });
      expect(seed).toBeNull();
    });

    it("returns null when defaults is null", () => {
      const seed = getConnectorWizardInitialValueFromDefaults("github", null);
      expect(seed).toBeNull();
    });

    it("returns null for connectors that ship no defaults projector", () => {
      const seed = getConnectorWizardInitialValueFromDefaults(
        "no-such-connector",
        {
          labels: { include: ["x"], exclude: [] },
        },
      );
      expect(seed).toBeNull();
    });
  });
});
