import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { UnitNode } from "../aggregate";

const useMemoriesMock = vi.fn();
vi.mock("@/lib/api/queries", () => ({
  useMemories: (scope: string, id: string) => useMemoriesMock(scope, id),
}));

import UnitMemoryTab from "./unit-memory";

describe("UnitMemoryTab", () => {
  const node: UnitNode = {
    kind: "Unit",
    id: "engineering",
    name: "Engineering",
    status: "running",
  };

  it("renders the v2.1 empty state when both lists are empty", () => {
    useMemoriesMock.mockReturnValueOnce({
      data: { shortTerm: [], longTerm: [] },
      isLoading: false,
      error: null,
    });
    render(<UnitMemoryTab node={node} path={[node]} />);
    expect(useMemoriesMock).toHaveBeenCalledWith("unit", "engineering");
    expect(screen.getByTestId("tab-unit-memory-empty")).toHaveTextContent(
      "No memory entries",
    );
    expect(screen.getByTestId("tab-unit-memory-empty")).toHaveTextContent(
      "v2.1",
    );
  });
});
