import { describe, expect, it } from "vitest";

import {
  aggregate,
  AGENT_TABS,
  filterTree,
  findIndex,
  flattenTree,
  overflowTabsFor,
  tabsFor,
  TENANT_TABS,
  UNIT_TABS,
  visibleTabsFor,
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

  it("surfaces a failure buried four levels deep at the root", () => {
    // Tenant → Unit → Unit → Unit → Agent(error). The failing agent sits at
    // depth 4 under the root; every intermediate node is "running". The
    // root aggregate must still report `worst === "error"` so the collapsed
    // top-level row can paint the danger severity without the operator
    // expanding the subtree.
    const sampleDeepTree: TreeNode = {
      id: "tenant-deep",
      name: "Deep Tenant",
      kind: "Tenant",
      status: "running",
      children: [
        {
          id: "unit-l1",
          name: "L1",
          kind: "Unit",
          status: "running",
          children: [
            {
              id: "unit-l2",
              name: "L2",
              kind: "Unit",
              status: "running",
              children: [
                {
                  id: "unit-l3",
                  name: "L3",
                  kind: "Unit",
                  status: "running",
                  children: [
                    {
                      id: "agent-buried",
                      name: "Buried",
                      kind: "Agent",
                      status: "error",
                    },
                  ],
                },
              ],
            },
          ],
        },
      ],
    };
    expect(aggregate(sampleDeepTree).worst).toBe("error");
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
  it("returns the flat concat of visible + overflow for each kind", () => {
    expect(tabsFor("Tenant")).toEqual([
      ...TENANT_TABS.visible,
      ...TENANT_TABS.overflow,
    ]);
    expect(tabsFor("Unit")).toEqual([
      ...UNIT_TABS.visible,
      ...UNIT_TABS.overflow,
    ]);
    expect(tabsFor("Agent")).toEqual([
      ...AGENT_TABS.visible,
      ...AGENT_TABS.overflow,
    ]);
  });

  it("locks the unit tab order and count (#2271/#2272 — Skills+Traces added, canonical order)", () => {
    // Canonical order per docs/design/canonical-tabs.md § 7.1: Skills
    // and Traces sit between Agents and Policies. Activity moved up to
    // slot 2 (matching Agent + Tenant) and Agents moved down after
    // Memory so the reading slots (Overview/Activity/Messages/Memory)
    // are contiguous across all subjects.
    expect([...UNIT_TABS.visible, ...UNIT_TABS.overflow]).toEqual([
      "Overview",
      "Activity",
      "Messages",
      "Memory",
      "Agents",
      "Skills",
      "Traces",
      "Policies",
      "Config",
    ]);
    expect(UNIT_TABS.visible).toHaveLength(8);
    expect(UNIT_TABS.overflow).toEqual(["Config"]);
  });

  it("locks the agent tab order and count (#1119 adds Deployment tab)", () => {
    expect([...AGENT_TABS.visible, ...AGENT_TABS.overflow]).toEqual([
      "Overview",
      "Activity",
      "Messages",
      "Memory",
      "Skills",
      "Traces",
      "Clones",
      "Policies",
      "Config",
      "Deployment",
    ]);
    expect(AGENT_TABS.overflow).toEqual([]);
  });

  it("locks the tenant tab order and count (#2257 — Memory removed)", () => {
    // Memory is intentionally absent on Tenant — Tenant does not have
    // memory (canonical-tabs.md § 1 / § 4.1). Messages, Agents, Skills,
    // Traces, Clones, and Deployment are also intentionally absent.
    expect([...TENANT_TABS.visible, ...TENANT_TABS.overflow]).toEqual([
      "Overview",
      "Activity",
      "Policies",
      "Budgets",
    ]);
    expect(TENANT_TABS.overflow).toEqual([]);
    expect(TENANT_TABS.visible).not.toContain("Memory");
  });
});

describe("visibleTabsFor / overflowTabsFor", () => {
  it("splits the Unit catalog into 8 visible + 1 overflow (#2271/#2272)", () => {
    expect(visibleTabsFor("Unit")).toEqual([
      "Overview",
      "Activity",
      "Messages",
      "Memory",
      "Agents",
      "Skills",
      "Traces",
      "Policies",
    ]);
    expect(overflowTabsFor("Unit")).toEqual(["Config"]);
  });

  it("surfaces the full Agent catalog as visible with no overflow (#1119 added Deployment → 10)", () => {
    expect(visibleTabsFor("Agent")).toHaveLength(10);
    expect(overflowTabsFor("Agent")).toEqual([]);
    // Deployment tab must be in the catalog.
    expect(visibleTabsFor("Agent")).toContain("Deployment");
  });

  it("surfaces the Tenant catalog as visible with no overflow (#2257)", () => {
    // 4 visible: Overview, Activity, Policies, Budgets. Config will be
    // added as overflow under #2254 (Config unification, sibling PR).
    expect(visibleTabsFor("Tenant")).toHaveLength(4);
    expect(overflowTabsFor("Tenant")).toEqual([]);
  });

  it("keeps `tabsFor` = [...visible, ...overflow] for every kind", () => {
    for (const kind of ["Tenant", "Unit", "Agent"] as const) {
      expect(tabsFor(kind)).toEqual([
        ...visibleTabsFor(kind),
        ...overflowTabsFor(kind),
      ]);
    }
  });
});

describe("filterTree", () => {
  it("returns the tree verbatim and an empty match set for an empty query", () => {
    const result = filterTree(sampleTree, "");
    expect(result.tree).toBe(sampleTree);
    expect(result.matches.size).toBe(0);
  });

  it("treats whitespace-only queries as empty", () => {
    const result = filterTree(sampleTree, "   \t  ");
    expect(result.tree).toBe(sampleTree);
    expect(result.matches.size).toBe(0);
  });

  it("matches case-insensitively as a substring of `name`", () => {
    // "AD" in upper-case must still match "Ada" — the comparison
    // lower-cases both sides.
    const result = filterTree(sampleTree, "AD");
    expect(result.tree).not.toBeNull();
    expect(result.matches.has("agent-ada")).toBe(true);
    // Margaret / Grace don't contain "ad" anywhere.
    expect(result.matches.has("agent-margaret")).toBe(false);
    expect(result.matches.has("agent-grace")).toBe(false);
  });

  it("preserves ancestors of matching nodes", () => {
    const result = filterTree(sampleTree, "grace");
    // Tenant → Engineering → Platform → Grace must all survive.
    const ids = new Set<string>();
    if (result.tree) {
      for (const { node } of flattenTree(result.tree)) ids.add(node.id);
    }
    expect(ids.has("tenant-acme")).toBe(true);
    expect(ids.has("unit-eng")).toBe(true);
    expect(ids.has("unit-platform")).toBe(true);
    expect(ids.has("agent-grace")).toBe(true);
    // Siblings of the match (Ada, Margaret) are pruned out — only the
    // ancestor chain is preserved, not unrelated branches.
    expect(ids.has("agent-ada")).toBe(false);
    expect(ids.has("agent-margaret")).toBe(false);
  });

  it("keeps every descendant when an ancestor's own name matches", () => {
    const result = filterTree(sampleTree, "engineering");
    expect(result.tree).not.toBeNull();
    const ids = new Set<string>();
    if (result.tree) {
      for (const { node } of flattenTree(result.tree)) ids.add(node.id);
    }
    // Engineering matches → every descendant survives so operators can
    // still drill into the branch that prompted the search.
    expect(ids.has("unit-eng")).toBe(true);
    expect(ids.has("agent-ada")).toBe(true);
    expect(ids.has("agent-margaret")).toBe(true);
    expect(ids.has("unit-platform")).toBe(true);
    expect(ids.has("agent-grace")).toBe(true);
  });

  it("returns a null tree when nothing matches", () => {
    const result = filterTree(sampleTree, "no-such-node");
    expect(result.tree).toBeNull();
    expect(result.matches.size).toBe(0);
  });

  it("does not mutate the source tree", () => {
    const before = JSON.stringify(sampleTree);
    filterTree(sampleTree, "ada");
    expect(JSON.stringify(sampleTree)).toBe(before);
  });
});
