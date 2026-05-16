import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { UnitNode } from "../aggregate";

// The wrapper just delegates to the canonical `<ConfigTab>`. We mock
// the canonical control to expose the props the wrapper hands down so
// the test can assert the wire shape without re-testing the sub-tab
// strip (covered in tab-impls/config-tab.test.tsx).
vi.mock("@/components/units/tab-impls/config-tab", () => ({
  ConfigTab: (props: {
    kind: string;
    id: string;
  }) => (
    <div
      data-testid="canonical-config-tab"
      data-kind={props.kind}
      data-id={props.id}
    />
  ),
}));

import UnitConfigTab from "./unit-config";

const unit: UnitNode = {
  kind: "Unit",
  id: "engineering",
  name: "Engineering",
  status: "running",
};

describe("UnitConfigTab — canonical wrapper (#2254)", () => {
  it("wires the canonical ConfigTab with the unit's id", () => {
    render(<UnitConfigTab node={unit} path={[unit]} />);
    const canonical = screen.getByTestId("canonical-config-tab");
    expect(canonical.dataset.kind).toBe("Unit");
    expect(canonical.dataset.id).toBe("engineering");
  });

  it("returns null when the node is not a Unit (defensive — registry guards this)", () => {
    const node = {
      kind: "Tenant",
      id: "tenant",
      name: "Tenant",
      status: "running",
    } as never;
    const { container } = render(
      <UnitConfigTab node={node} path={[node]} />,
    );
    expect(container.firstChild).toBeNull();
  });
});
