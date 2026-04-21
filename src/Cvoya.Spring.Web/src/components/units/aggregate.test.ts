import { describe, expect, it } from "vitest";

import {
  aggregate,
  AGENT_TABS,
  findIndex,
  flattenTree,
  tabsFor,
  TENANT_TABS,
  UNIT_TABS,
  type TreeNode,
} from "./aggregate";

const sampleTree: TreeNode = {
  id: "tenant-acme",
  name: "Acme",
  kind: "Tenant",
  status: "running",
  children: [
    {
      id: "unit-eng",
      name: "Engineering",
      kind: "Unit",
      status: "running",
      cost24h: 1.0,
      msgs24h: 100,
      children: [
        {
          id: "agent-ada",
          name: "Ada",
          kind: "Agent",
          status: "running",
          cost24h: 0.5,
          msgs24h: 50,
        },
        {
          id: "agent-margaret",
          name: "Margaret",
          kind: "Agent",
          status: "starting",
          cost24h: 0.0,
          msgs24h: 0,
        },
        {
          id: "unit-platform",
          name: "Platform",
          kind: "Unit",
          status: "running",
          cost24h: 2.0,
          msgs24h: 200,
          children: [
            {
              id: "agent-grace",
              name: "Grace",
              kind: "Agent",
              status: "error",
              cost24h: 0.25,
              msgs24h: 10,
            },
          ],
        },
      ],
    },
  ],
};

describe("aggregate", () => {
  it("rolls cost, messages, agent count, and unit count up the subtree", () => {
    const sub = aggregate(sampleTree);
    expect(sub.cost).toBeCloseTo(3.75, 4);
    expect(sub.msgs).toBe(360);
    expect(sub.agents).toBe(3);
    expect(sub.units).toBe(2);
  });

  it("surfaces the worst-status-in-subtree", () => {
    expect(aggregate(sampleTree).worst).toBe("error");
  });

  it("returns the node's own status when it has no children", () => {
    const leaf = aggregate({
      id: "leaf",
      name: "Leaf",
      kind: "Agent",
      status: "paused",
    });
    expect(leaf.worst).toBe("paused");
    expect(leaf.cost).toBe(0);
    expect(leaf.msgs).toBe(0);
    expect(leaf.agents).toBe(1);
    expect(leaf.units).toBe(0);
  });

  it("treats undefined cost24h / msgs24h as 0 without coercing other values", () => {
    const node: TreeNode = {
      id: "u",
      name: "U",
      kind: "Unit",
      status: "running",
      children: [
        { id: "a", name: "A", kind: "Agent", status: "running", cost24h: 1.5 },
      ],
    };
    const sub = aggregate(node);
    expect(sub.cost).toBe(1.5);
    expect(sub.msgs).toBe(0);
  });
});

describe("flattenTree / findIndex", () => {
  it("returns nodes in depth-first order with full ancestry path", () => {
    const all = flattenTree(sampleTree);
    expect(all.map((e) => e.node.id)).toEqual([
      "tenant-acme",
      "unit-eng",
      "agent-ada",
      "agent-margaret",
      "unit-platform",
      "agent-grace",
    ]);
    const grace = all.find((e) => e.node.id === "agent-grace");
    expect(grace?.path.map((p) => p.id)).toEqual([
      "tenant-acme",
      "unit-eng",
      "unit-platform",
      "agent-grace",
    ]);
  });

  it("indexes every node by id for O(1) lookup", () => {
    const { byId } = findIndex(sampleTree);
    expect(byId["agent-ada"]?.node.name).toBe("Ada");
    expect(byId["agent-grace"]?.path).toHaveLength(4);
    expect(byId["does-not-exist"]).toBeUndefined();
  });
});

describe("tabsFor", () => {
  it("returns the locked v2.0 tab catalog for each kind", () => {
    expect(tabsFor("Tenant")).toBe(TENANT_TABS);
    expect(tabsFor("Unit")).toBe(UNIT_TABS);
    expect(tabsFor("Agent")).toBe(AGENT_TABS);
  });

  it("locks the unit tab order and count (per plan §3 — v2.0 disposition)", () => {
    expect(UNIT_TABS).toEqual([
      "Overview",
      "Agents",
      "Orchestration",
      "Activity",
      "Messages",
      "Memory",
      "Policies",
      "Config",
    ]);
  });

  it("locks the agent tab order and count", () => {
    expect(AGENT_TABS).toEqual([
      "Overview",
      "Activity",
      "Messages",
      "Memory",
      "Skills",
      "Traces",
      "Clones",
      "Config",
    ]);
  });

  it("locks the tenant tab order and count", () => {
    expect(TENANT_TABS).toEqual([
      "Overview",
      "Activity",
      "Policies",
      "Budgets",
      "Memory",
    ]);
  });
});
