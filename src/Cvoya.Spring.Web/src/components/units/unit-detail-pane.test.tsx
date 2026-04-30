import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// Same posture as `unit-explorer.test.tsx`: stub the pane actions cluster so
// we don't have to wire a TanStack Query client + Next router mock for tests
// that only care about the pane chrome (#1070).
vi.mock("./unit-pane-actions", () => ({
  UnitPaneActions: () => null,
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
