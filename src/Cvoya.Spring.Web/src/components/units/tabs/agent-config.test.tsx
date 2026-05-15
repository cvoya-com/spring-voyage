import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { AgentNode } from "../aggregate";

// The wrapper's only job is to look up `useAgent(id)` for the parent
// unit + raw status and hand them to the canonical `<ConfigTab>`.
// We mock `<ConfigTab>` to expose the props the wrapper hands down so
// the test can assert the wire shape without re-testing the sub-tab
// strip (covered in tab-impls/config-tab.test.tsx).
vi.mock("@/components/units/tab-impls/config-tab", () => ({
  ConfigTab: (props: {
    kind: string;
    id: string;
    name: string;
    parentUnitId?: string | null;
    status?: unknown;
  }) => (
    <div
      data-testid="canonical-config-tab"
      data-kind={props.kind}
      data-id={props.id}
      data-name={props.name}
      data-parent-unit-id={props.parentUnitId ?? ""}
      data-status={
        props.status == null ? "" : JSON.stringify(props.status)
      }
    />
  ),
}));

const useAgentMock = vi.fn();
vi.mock("@/lib/api/queries", () => ({
  useAgent: (id: string) => useAgentMock(id),
}));

import AgentConfigTab from "./agent-config";

describe("AgentConfigTab — canonical wrapper (#2254)", () => {
  it("wires the canonical ConfigTab with the agent's parent unit + status", () => {
    useAgentMock.mockReturnValueOnce({
      data: {
        agent: { parentUnit: "engineering" },
        status: { mode: "Auto", running: true },
      },
    });
    const node: AgentNode = {
      kind: "Agent",
      id: "ada",
      name: "Ada",
      status: "running",
    };
    render(<AgentConfigTab node={node} path={[node]} />);

    const canonical = screen.getByTestId("canonical-config-tab");
    expect(canonical.dataset.kind).toBe("Agent");
    expect(canonical.dataset.id).toBe("ada");
    expect(canonical.dataset.name).toBe("Ada");
    expect(canonical.dataset.parentUnitId).toBe("engineering");
    expect(canonical.dataset.status).toContain('"mode":"Auto"');
  });

  it("falls back to a null parent unit when useAgent has not yet resolved", () => {
    useAgentMock.mockReturnValueOnce({ data: undefined });
    const node: AgentNode = {
      kind: "Agent",
      id: "ada",
      name: "Ada",
      status: "running",
    };
    render(<AgentConfigTab node={node} path={[node]} />);

    const canonical = screen.getByTestId("canonical-config-tab");
    expect(canonical.dataset.parentUnitId).toBe("");
  });

  it("returns null when the node is not an Agent (defensive — registry guards this)", () => {
    useAgentMock.mockReturnValueOnce({ data: undefined });
    const node = {
      kind: "Unit",
      id: "engineering",
      name: "Engineering",
      status: "running",
    } as never;
    const { container } = render(
      <AgentConfigTab node={node} path={[node]} />,
    );
    expect(container.firstChild).toBeNull();
  });
});
