import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import {
  __resetTabRegistryForTesting,
  lookupTab,
  registerTab,
  registeredTabs,
  tabKey,
} from "./index";

describe("tabs registry (FOUND-tabscaffold)", () => {
  beforeEach(() => __resetTabRegistryForTesting());
  afterEach(() => __resetTabRegistryForTesting());

  it("starts empty so the foundation PR ships zero tab registrations", () => {
    expect(registeredTabs()).toEqual([]);
  });

  it("composes a typed `<kind>.<tab>` key", () => {
    expect(tabKey("Unit", "Overview")).toBe("Unit.Overview");
    expect(tabKey("Agent", "Skills")).toBe("Agent.Skills");
    expect(tabKey("Tenant", "Budgets")).toBe("Tenant.Budgets");
  });

  it("returns null when no component has been registered", () => {
    expect(lookupTab("Unit", "Overview")).toBeNull();
  });

  it("allows registering a per-(kind, tab) component", () => {
    const Overview = () => null;
    registerTab("Unit", "Overview", Overview);
    expect(lookupTab("Unit", "Overview")).toBe(Overview);
    expect(registeredTabs()).toContain("Unit.Overview");
  });

  it("keeps registrations isolated per (kind, tab) pair", () => {
    const UnitMessages = () => null;
    const AgentMessages = () => null;
    registerTab("Unit", "Messages", UnitMessages);
    registerTab("Agent", "Messages", AgentMessages);
    expect(lookupTab("Unit", "Messages")).toBe(UnitMessages);
    expect(lookupTab("Agent", "Messages")).toBe(AgentMessages);
    expect(lookupTab("Tenant", "Activity")).toBeNull();
  });

  it("overwrites on duplicate registration outside production (HMR-safe)", () => {
    const First = () => null;
    const Second = () => null;
    const warn = vi.spyOn(console, "warn").mockImplementation(() => {});
    try {
      registerTab("Unit", "Overview", First);
      registerTab("Unit", "Overview", Second);
      expect(lookupTab("Unit", "Overview")).toBe(Second);
      expect(warn).toHaveBeenCalledTimes(1);
    } finally {
      warn.mockRestore();
    }
  });

  it("throws on duplicate registration in production", () => {
    const prev = process.env.NODE_ENV;
    // vitest's env type disallows assignment, but we need to simulate a prod
    // runtime just for this test; restore in the finally block.
    (process.env as Record<string, string | undefined>).NODE_ENV = "production";
    try {
      const First = () => null;
      const Second = () => null;
      registerTab("Unit", "Overview", First);
      expect(() => registerTab("Unit", "Overview", Second)).toThrow(
        /duplicate registration/i,
      );
    } finally {
      (process.env as Record<string, string | undefined>).NODE_ENV = prev;
    }
  });
});
