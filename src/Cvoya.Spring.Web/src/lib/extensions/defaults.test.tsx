// Locks the v2 IA (plan §2 of umbrella #815) at the manifest layer so
// reordering groups or dropping load-bearing routes fails the build
// instead of the user's muscle memory. Downstream surfaces (sidebar,
// command palette) read these defaults; pinning the shape here means
// a single intentional edit flows everywhere.

import { describe, expect, it } from "vitest";

import { defaultActions, defaultRoutes } from "./defaults";
import { NAV_SECTION_ORDER } from "./types";

describe("defaultRoutes (IA §2)", () => {
  it("ships the Overview / Orchestrate / Control / Settings nav order", () => {
    expect(NAV_SECTION_ORDER).toEqual([
      "overview",
      "orchestrate",
      "control",
      "settings",
    ]);
  });

  it("groups the 11 v2 sidebar items into their clusters", () => {
    const byPath = Object.fromEntries(
      defaultRoutes.map((r) => [r.path, r.navSection]),
    );

    expect(byPath["/"]).toBe("overview");
    expect(byPath["/activity"]).toBe("overview");
    expect(byPath["/analytics"]).toBe("overview");

    expect(byPath["/units"]).toBe("orchestrate");
    expect(byPath["/inbox"]).toBe("orchestrate");
    expect(byPath["/discovery"]).toBe("orchestrate");
    // #1454: Engagement is the latest Orchestrate entry, sitting
    // immediately below Discovery and marked experimental.
    expect(byPath["/engagement"]).toBe("orchestrate");

    expect(byPath["/connectors"]).toBe("control");
    expect(byPath["/policies"]).toBe("control");
    expect(byPath["/budgets"]).toBe("control");
    expect(byPath["/settings"]).toBe("control");
  });

  it("renders the Engagement entry directly below Discovery in the Orchestrate cluster (#1454)", () => {
    const orchestrate = defaultRoutes
      .filter((r) => r.navSection === "orchestrate")
      .sort((a, b) => (a.orderHint ?? 0) - (b.orderHint ?? 0));

    const discoveryIdx = orchestrate.findIndex((r) => r.path === "/discovery");
    const engagementIdx = orchestrate.findIndex((r) => r.path === "/engagement");
    expect(discoveryIdx).toBeGreaterThanOrEqual(0);
    expect(engagementIdx).toBe(discoveryIdx + 1);

    const engagement = orchestrate[engagementIdx]!;
    expect(engagement.label).toBe("Engagement");
    expect(engagement.secondaryLabel).toMatch(/experimental/i);
  });

  it("drops the routes retired by the v2 IA", () => {
    const paths = defaultRoutes.map((r) => r.path);
    // §2: deleted top-level entries (pages may still exist until DEL-*
    // issues land, but the sidebar manifest no longer surfaces them).
    for (const gone of [
      "/agents",
      "/conversations",
      "/initiative",
      "/packages",
      "/system/configuration",
      "/admin/agent-runtimes",
      "/admin/connectors",
    ]) {
      expect(paths).not.toContain(gone);
    }
  });

  it("renames /directory to /discovery", () => {
    const paths = defaultRoutes.map((r) => r.path);
    expect(paths).not.toContain("/directory");
    expect(paths).toContain("/discovery");
  });
});

describe("defaultActions (palette)", () => {
  it("includes a Settings-hub entry pointing at /settings", () => {
    const settings = defaultActions.find((a) => a.id === "settings.open");
    expect(settings).toBeDefined();
    expect(settings!.href).toBe("/settings");
  });

  it("renames the discovery action and points it at /discovery", () => {
    const ids = defaultActions.map((a) => a.id);
    expect(ids).not.toContain("directory.expertise");
    const discovery = defaultActions.find((a) => a.id === "discovery.expertise");
    expect(discovery).toBeDefined();
    expect(discovery!.href).toBe("/discovery");
  });

  it("routes agent- and conversation-list actions through the Explorer", () => {
    // §4 folds the former `/agents` and `/conversations` surfaces into
    // the Explorer's Agents + Messages tabs; the palette shortcuts now
    // deep-link into `/units` instead.
    expect(defaultActions.find((a) => a.id === "agent.list")!.href).toBe(
      "/units",
    );
    expect(
      defaultActions.find((a) => a.id === "thread.list")!.href,
    ).toBe("/units");
  });
});
