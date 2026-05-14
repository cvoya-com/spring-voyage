import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { TenantNode } from "../aggregate";

// The wrapper just delegates to the canonical `<ConfigTab>`. We mock
// the canonical control to expose the props the wrapper hands down so
// the test can assert the wire shape without re-testing the sub-tab
// strip (covered in tab-impls/config-tab.test.tsx).
vi.mock("@/components/units/tab-impls/config-tab", () => ({
  ConfigTab: (props: {
    kind: string;
    id: string;
    name: string;
  }) => (
    <div
      data-testid="canonical-config-tab"
      data-kind={props.kind}
      data-id={props.id}
      data-name={props.name}
    />
  ),
}));

import TenantConfigTab from "./tenant-config";

const tenant: TenantNode = {
  kind: "Tenant",
  id: "tenant",
  name: "Tenant",
  status: "running",
};

describe("TenantConfigTab — new canonical surface (#2254)", () => {
  it("wires the canonical ConfigTab with the tenant's id + name", () => {
    render(<TenantConfigTab node={tenant} path={[tenant]} />);
    const canonical = screen.getByTestId("canonical-config-tab");
    expect(canonical.dataset.kind).toBe("Tenant");
    expect(canonical.dataset.id).toBe("tenant");
    expect(canonical.dataset.name).toBe("Tenant");
  });

  it("returns null when the node is not a Tenant (defensive — registry guards this)", () => {
    const node = {
      kind: "Unit",
      id: "engineering",
      name: "Engineering",
      status: "running",
    } as never;
    const { container } = render(
      <TenantConfigTab node={node} path={[node]} />,
    );
    expect(container.firstChild).toBeNull();
  });
});
