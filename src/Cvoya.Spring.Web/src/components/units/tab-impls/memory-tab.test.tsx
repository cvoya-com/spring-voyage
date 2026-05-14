import { render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

const useMemoriesMock = vi.fn();
vi.mock("@/lib/api/queries", () => ({
  useMemories: (scope: string, id: string) => useMemoriesMock(scope, id),
}));

import { MemoryTab } from "./memory-tab";

describe("MemoryTab", () => {
  beforeEach(() => {
    useMemoriesMock.mockReset();
  });

  it("calls useMemories with scope=unit when kind=Unit", () => {
    useMemoriesMock.mockReturnValueOnce({
      data: { shortTerm: [], longTerm: [] },
      isLoading: false,
      error: null,
    });
    render(<MemoryTab kind="Unit" id="engineering" />);
    expect(useMemoriesMock).toHaveBeenCalledWith("unit", "engineering");
    expect(screen.getByTestId("tab-unit-memory-empty")).toHaveTextContent(
      "v2.1",
    );
  });

  it("calls useMemories with scope=agent when kind=Agent", () => {
    useMemoriesMock.mockReturnValueOnce({
      data: { shortTerm: [], longTerm: [] },
      isLoading: false,
      error: null,
    });
    render(<MemoryTab kind="Agent" id="ada" />);
    expect(useMemoriesMock).toHaveBeenCalledWith("agent", "ada");
    expect(screen.getByTestId("tab-agent-memory-empty")).toHaveTextContent(
      "v2.1",
    );
  });

  it("renders the loading state", () => {
    useMemoriesMock.mockReturnValueOnce({
      data: undefined,
      isLoading: true,
      error: null,
    });
    render(<MemoryTab kind="Unit" id="engineering" />);
    expect(screen.getByTestId("tab-unit-memory-loading")).toHaveTextContent(
      "Loading memory entries",
    );
  });

  it("renders the error state", () => {
    useMemoriesMock.mockReturnValueOnce({
      data: undefined,
      isLoading: false,
      error: new Error("boom"),
    });
    render(<MemoryTab kind="Agent" id="ada" />);
    expect(screen.getByTestId("tab-agent-memory-error")).toBeInTheDocument();
  });

  it("renders short-term and long-term entries when present", () => {
    useMemoriesMock.mockReturnValueOnce({
      data: {
        shortTerm: [
          {
            id: "s1",
            content: "remember the milk",
            createdAt: new Date().toISOString(),
            source: "ada",
          },
        ],
        longTerm: [
          {
            id: "l1",
            content: "Pi is roughly 3.14",
            createdAt: new Date().toISOString(),
            source: null,
          },
        ],
      },
      isLoading: false,
      error: null,
    });
    render(<MemoryTab kind="Unit" id="engineering" />);
    expect(screen.getByTestId("tab-unit-memory")).toBeInTheDocument();
    expect(screen.getByText("remember the milk")).toBeInTheDocument();
    expect(screen.getByText("Pi is roughly 3.14")).toBeInTheDocument();
    expect(screen.getByText("Short-term")).toBeInTheDocument();
    expect(screen.getByText("Long-term")).toBeInTheDocument();
  });
});
