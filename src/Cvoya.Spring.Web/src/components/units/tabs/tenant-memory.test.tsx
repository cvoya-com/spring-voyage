import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import type { TenantNode } from "../aggregate";

import TenantMemoryTab from "./tenant-memory";

describe("TenantMemoryTab", () => {
  it("renders the static v2.1 placeholder", () => {
    const node: TenantNode = {
      kind: "Tenant",
      id: "tenant",
      name: "Tenant",
      status: "running",
    };
    render(<TenantMemoryTab node={node} path={[node]} />);
    expect(screen.getByTestId("tab-tenant-memory-empty")).toHaveTextContent(
      "v2.1",
    );
  });
});
