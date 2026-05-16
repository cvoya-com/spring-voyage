import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// Same posture as `unit-explorer.test.tsx`: stub the pane actions cluster so
// we don't have to wire a TanStack Query client + Next router mock for tests
// that only care about the pane chrome (#1070).
vi.mock("./unit-pane-actions", () => ({
  UnitPaneActions: () => null,
}));

// #2372 / #2388: the header status cluster reads live LifecycleStatus
// through `useUnit` / `useAgent`. Stub via mutable refs so individual tests
// can dictate what each hook resolves with — same posture as the
// pane-actions stub above (no TanStack QueryClient needed).
const useUnitMock = vi.fn<(id: string) => { data: unknown }>(() => ({
  data: undefined,
}));
const useAgentMock = vi.fn<(id: string) => { data: unknown }>(() => ({
  data: undefined,
}));
vi.mock("@/lib/api/queries", () => ({
  useUnit: (id: string) => useUnitMock(id),
  useAgent: (id: string) => useAgentMock(id),
}));

import type { TreeNode } from "./aggregate";
import { __resetTabRegistryForTesting } from "./tabs";
import { addressFor, DetailPane } from "./unit-detail-pane";

const tenant: TreeNode = {
  id: "tenant://acme",
  name: "Acme",
  kind: "Tenant",
  status: "running",
};

const unit: TreeNode = {
  id: "engineering",
  name: "Engineering",
  kind: "Unit",
  status: "running",
};

const agent: TreeNode = {
  id: "ada",
  name: "Ada",
  kind: "Agent",
  status: "running",
};

function setupClipboard() {
  const writeText = vi.fn().mockResolvedValue(undefined);
  Object.defineProperty(navigator, "clipboard", {
    configurable: true,
    value: { writeText },
  });
  return writeText;
}

describe("addressFor (#1070 / #1200)", () => {
  it("returns the canonical id verbatim when it already carries a known scheme", () => {
    expect(addressFor(tenant)).toBe("tenant://acme");
    expect(
      addressFor({ ...unit, id: "unit://engineering" } as TreeNode),
    ).toBe("unit://engineering");
    expect(addressFor({ ...agent, id: "agent://ada" } as TreeNode)).toBe(
      "agent://ada",
    );
  });

  it("prefixes bare ids with the kind's scheme (no path)", () => {
    expect(addressFor(unit)).toBe("unit://engineering");
    expect(addressFor(agent)).toBe("agent://ada");
    expect(addressFor({ ...tenant, id: "default" } as TreeNode)).toBe(
      "tenant://default",
    );
  });

  // #1200: agent addresses include the unit-path prefix so the copied
  // identity is globally unique across tenants with multiple units.
  it("includes the unit-path prefix for an agent when path is supplied", () => {
    const path: TreeNode[] = [tenant, unit, agent];
    expect(addressFor(agent, path)).toBe("agent://engineering/ada");
  });

  it("includes nested unit segments for a deeply-nested agent", () => {
    const subUnit: TreeNode = {
      id: "frontend",
      name: "Frontend",
      kind: "Unit",
      status: "running",
    };
    const path: TreeNode[] = [tenant, unit, subUnit, agent];
    expect(addressFor(agent, path)).toBe("agent://engineering/frontend/ada");
  });

  it("falls back to agent://<name> when path has no unit ancestors", () => {
    // Agent directly under the tenant root (no unit in the path).
    const path: TreeNode[] = [tenant, agent];
    expect(addressFor(agent, path)).toBe("agent://ada");
  });

  it("does not alter unit or tenant addresses when path is supplied", () => {
    const path: TreeNode[] = [tenant, unit];
    expect(addressFor(unit, path)).toBe("unit://engineering");
    expect(addressFor(tenant, path)).toBe("tenant://acme");
  });
});

describe("DetailPane copy-address button (#1070)", () => {
  beforeEach(() => __resetTabRegistryForTesting());
  afterEach(() => {
    __resetTabRegistryForTesting();
    vi.restoreAllMocks();
  });

  it("renders next to the breadcrumb with the address in the aria-label", () => {
    render(
      <DetailPane
        node={unit}
        path={[tenant, unit]}
        tab="Overview"
        onTabChange={vi.fn()}
        onSelectNode={vi.fn()}
      />,
    );
    const btn = screen.getByTestId("detail-copy-address");
    expect(btn).toBeInTheDocument();
    expect(btn).toHaveAttribute(
      "aria-label",
      "Copy address unit://engineering",
    );
  });

  it("copies the full-path agent address when an agent is selected (#1200)", async () => {
    const writeText = setupClipboard();
    render(
      <DetailPane
        node={agent}
        path={[tenant, unit, agent]}
        tab="Overview"
        onTabChange={vi.fn()}
        onSelectNode={vi.fn()}
      />,
    );
    fireEvent.click(screen.getByTestId("detail-copy-address"));
    // #1200: the copied address includes the unit segment so the identity
    // is globally unique: agent://engineering/ada, not just agent://ada.
    await waitFor(() =>
      expect(writeText).toHaveBeenCalledWith("agent://engineering/ada"),
    );
  });

  it("copies the tenant address (already prefixed) when only the tenant root is selected", async () => {
    const writeText = setupClipboard();
    render(
      <DetailPane
        node={tenant}
        path={[tenant]}
        tab="Overview"
        onTabChange={vi.fn()}
        onSelectNode={vi.fn()}
      />,
    );
    fireEvent.click(screen.getByTestId("detail-copy-address"));
    await waitFor(() =>
      expect(writeText).toHaveBeenCalledWith("tenant://acme"),
    );
  });

  it("flips to the 'Address copied' aria-label after a successful copy", async () => {
    setupClipboard();
    render(
      <DetailPane
        node={unit}
        path={[tenant, unit]}
        tab="Overview"
        onTabChange={vi.fn()}
        onSelectNode={vi.fn()}
      />,
    );
    const btn = screen.getByTestId("detail-copy-address");
    fireEvent.click(btn);
    await waitFor(() =>
      expect(btn).toHaveAttribute("aria-label", "Address copied"),
    );
  });

  // #1548: an entity with no display name surfaces its actor UUID as the
  // node name. Rendering a bare UUID followed by "active" tells the user
  // nothing — fall back to the kind label as the title and demote the
  // UUID into a muted, monospace identifier so the header still reads.
  it("falls back to kind-as-title and shows an ID caption when name is UUID-shaped (#1548)", () => {
    const uuidName = "11111111-2222-3333-4444-555555555555";
    const uuidUnit: TreeNode = {
      id: uuidName,
      name: uuidName,
      kind: "Unit",
      status: "running",
    };
    render(
      <DetailPane
        node={uuidUnit}
        path={[tenant, uuidUnit]}
        tab="Overview"
        onTabChange={vi.fn()}
        onSelectNode={vi.fn()}
      />,
    );
    expect(screen.getByTestId("detail-title")).toHaveTextContent("Unit");
    const idCaption = screen.getByTestId("detail-title-id");
    expect(idCaption).toHaveTextContent(uuidName);
  });

  it("renders the node name verbatim when it is a normal display name (#1548)", () => {
    render(
      <DetailPane
        node={unit}
        path={[tenant, unit]}
        tab="Overview"
        onTabChange={vi.fn()}
        onSelectNode={vi.fn()}
      />,
    );
    expect(screen.getByTestId("detail-title")).toHaveTextContent("Engineering");
    expect(screen.queryByTestId("detail-title-id")).not.toBeInTheDocument();
  });

  // #1785: when the parent's URL-driven `onTabChange` identity churns on
  // every URL update (the prod bug: `searchParams → writeUrl →
  // handleTabChange` returns a new function each render), the tab-
  // correction `useEffect` must not re-fire and re-call `onTabChange` —
  // doing so creates a tight loop that pegs the main thread. The
  // correction should fire exactly once per `isValidTab → false`
  // transition, not once per `onTabChange` identity change.
  it("#1785: correction fires exactly once per isValidTab→false transition, not on every onTabChange identity change", () => {
    const onTabChangeA = vi.fn();
    // `Skills` is not in the Tenant tab catalog, so isValidTab=false
    // and the correction effect should fire once on mount.
    const { rerender } = render(
      <DetailPane
        node={tenant}
        path={[tenant]}
        tab={"Skills" as never}
        onTabChange={onTabChangeA}
        onSelectNode={vi.fn()}
      />,
    );
    expect(onTabChangeA).toHaveBeenCalledTimes(1);

    // Re-render with a fresh `onTabChange` identity (mirroring the
    // prod URL-update churn) but the same invalid `tab` and `node`.
    // The correction effect must NOT re-fire — neither the previous
    // nor the new callback should be invoked again.
    const onTabChangeB = vi.fn();
    rerender(
      <DetailPane
        node={tenant}
        path={[tenant]}
        tab={"Skills" as never}
        onTabChange={onTabChangeB}
        onSelectNode={vi.fn()}
      />,
    );
    expect(onTabChangeA).toHaveBeenCalledTimes(1);
    expect(onTabChangeB).not.toHaveBeenCalled();
  });

  it("swallows clipboard errors so the surface stays usable", async () => {
    const writeText = vi.fn().mockRejectedValue(new Error("denied"));
    Object.defineProperty(navigator, "clipboard", {
      configurable: true,
      value: { writeText },
    });
    render(
      <DetailPane
        node={unit}
        path={[tenant, unit]}
        tab="Overview"
        onTabChange={vi.fn()}
        onSelectNode={vi.fn()}
      />,
    );
    const btn = screen.getByTestId("detail-copy-address");
    fireEvent.click(btn);
    await waitFor(() => expect(writeText).toHaveBeenCalled());
    // No exception, button still rendered, label still in the "copy" state
    // (the success swap never fires because the promise rejected).
    expect(btn).toHaveAttribute(
      "aria-label",
      "Copy address unit://engineering",
    );
  });
});

// ---------------------------------------------------------------------------
// `DetailHeaderStatus` — issue #2388 regression suite.
//
// The detail-pane header renders a coloured dot + 7-state lifecycle badge
// for the selected node. The cluster reads live `LifecycleStatus` from
// `useUnit(id)` for units and `useAgent(id).agent.lifecycleStatus` for
// agents, falling back to the tree-side `node.status` when the per-kind
// detail query has not resolved.
//
// #2388 reports that the agent detail page header was rendering with no
// dot or badge for freshly-created agents. The badge component itself
// always renders SOMETHING (unknown statuses collapse to "Draft"), so the
// risk surface is upstream — a contract drift on the
// `AgentDetailResponse.agent.lifecycleStatus` field or a regression in
// the header's read path would cause the cluster to render against an
// empty value. These tests pin the live-read paths for both kinds.
// ---------------------------------------------------------------------------

describe("DetailHeaderStatus (#2388)", () => {
  beforeEach(() => {
    __resetTabRegistryForTesting();
    useUnitMock.mockReset();
    useAgentMock.mockReset();
    useUnitMock.mockReturnValue({ data: undefined });
    useAgentMock.mockReturnValue({ data: undefined });
  });
  afterEach(() => {
    __resetTabRegistryForTesting();
    vi.restoreAllMocks();
  });

  it("renders the dot + badge for a unit using the tree-side status while the unit query is unresolved", () => {
    // Mirrors the cold-load path: tree carries `status: "running"`, the
    // per-unit detail query has not yet returned. The badge must still
    // render so the operator sees the unit's lifecycle without waiting
    // for the round-trip.
    render(
      <DetailPane
        node={unit}
        path={[tenant, unit]}
        tab="Overview"
        onTabChange={vi.fn()}
        onSelectNode={vi.fn()}
      />,
    );
    expect(screen.getByTestId("detail-status-dot")).toHaveAttribute(
      "data-lifecycle-status",
      "Running",
    );
    expect(screen.getByTestId("detail-status-badge")).toHaveAttribute(
      "data-lifecycle-status",
      "Running",
    );
  });

  it("prefers the live unit status from `useUnit` over the tree-side value once the query resolves", () => {
    useUnitMock.mockReturnValue({
      // UnitResponse-shaped — `useUnit` returns the unwrapped envelope.
      data: { status: "Stopped" },
    });
    render(
      <DetailPane
        node={unit}
        path={[tenant, unit]}
        tab="Overview"
        onTabChange={vi.fn()}
        onSelectNode={vi.fn()}
      />,
    );
    expect(screen.getByTestId("detail-status-badge")).toHaveTextContent(
      "Stopped",
    );
    expect(screen.getByTestId("detail-status-dot")).toHaveAttribute(
      "data-lifecycle-status",
      "Stopped",
    );
  });

  it("renders the dot + badge for an agent using the tree-side status while the agent query is unresolved (#2388 cold-load)", () => {
    render(
      <DetailPane
        node={agent}
        path={[tenant, unit, agent]}
        tab="Overview"
        onTabChange={vi.fn()}
        onSelectNode={vi.fn()}
      />,
    );
    // Cold load: `useAgent` returns `{ data: undefined }` so the cluster
    // falls back to `node.status = "running"` from the tree. Neither the
    // dot nor the badge may be missing — that was the #2388 symptom.
    expect(screen.getByTestId("detail-status-dot")).toHaveAttribute(
      "data-lifecycle-status",
      "Running",
    );
    expect(screen.getByTestId("detail-status-badge")).toHaveTextContent(
      "Running",
    );
  });

  it("prefers the live agent lifecycleStatus from `useAgent` over the tree-side value (#2388 hot path)", () => {
    // AgentDetailResponse-shaped — the wire emits `lifecycleStatus` as
    // a lowercase string (see AgentEndpoints.ToAgentResponse). The badge
    // component normalises to PascalCase.
    useAgentMock.mockReturnValue({
      data: {
        agent: { lifecycleStatus: "running" },
        status: null,
        deployment: null,
      },
    });
    render(
      <DetailPane
        node={agent}
        path={[tenant, unit, agent]}
        tab="Overview"
        onTabChange={vi.fn()}
        onSelectNode={vi.fn()}
      />,
    );
    expect(screen.getByTestId("detail-status-dot")).toHaveAttribute(
      "data-lifecycle-status",
      "Running",
    );
    expect(screen.getByTestId("detail-status-badge")).toHaveTextContent(
      "Running",
    );
  });

  it("falls back to `node.status` when the agent envelope omits `lifecycleStatus` (server returned null)", () => {
    // The Show endpoint fail-opens to `lifecycleStatus: null` when the
    // actor proxy throws (see GetAgent_LifecycleStatus_NullWhenActorThrows
    // on the backend). The header must still render — the tree-side
    // status keeps the badge populated until the next refetch.
    useAgentMock.mockReturnValue({
      data: {
        agent: { lifecycleStatus: null },
        status: null,
        deployment: null,
      },
    });
    render(
      <DetailPane
        node={agent}
        path={[tenant, unit, agent]}
        tab="Overview"
        onTabChange={vi.fn()}
        onSelectNode={vi.fn()}
      />,
    );
    expect(screen.getByTestId("detail-status-dot")).toHaveAttribute(
      "data-lifecycle-status",
      "Running",
    );
    expect(screen.getByTestId("detail-status-badge")).toHaveTextContent(
      "Running",
    );
  });
});
