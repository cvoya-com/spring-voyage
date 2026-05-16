import { fireEvent, render, screen, within } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

const useMemoriesMock = vi.fn();
vi.mock("@/lib/api/queries", () => ({
  useMemories: (
    scope: string,
    id: string,
    options?: Record<string, unknown>,
  ) => useMemoriesMock(scope, id, options),
}));

import { MemoryTab } from "./memory-tab";

describe("MemoryTab", () => {
  beforeEach(() => {
    useMemoriesMock.mockReset();
  });

  it("calls useMemories with scope=unit when kind=Unit", () => {
    useMemoriesMock.mockReturnValue({
      data: { shortTerm: [], longTerm: [] },
      isLoading: false,
      error: null,
    });
    render(<MemoryTab kind="Unit" id="engineering" />);
    expect(useMemoriesMock).toHaveBeenCalledWith(
      "unit",
      "engineering",
      expect.objectContaining({}),
    );
    expect(screen.getByTestId("tab-unit-memory-empty")).toHaveTextContent(
      "No memory entries yet",
    );
  });

  it("calls useMemories with scope=agent when kind=Agent", () => {
    useMemoriesMock.mockReturnValue({
      data: { shortTerm: [], longTerm: [] },
      isLoading: false,
      error: null,
    });
    render(<MemoryTab kind="Agent" id="ada" />);
    expect(useMemoriesMock).toHaveBeenCalledWith(
      "agent",
      "ada",
      expect.objectContaining({}),
    );
    expect(screen.getByTestId("tab-agent-memory-empty")).toHaveTextContent(
      "No memory entries yet",
    );
  });

  it("renders the loading state", () => {
    useMemoriesMock.mockReturnValue({
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
    useMemoriesMock.mockReturnValue({
      data: undefined,
      isLoading: false,
      error: new Error("boom"),
    });
    render(<MemoryTab kind="Agent" id="ada" />);
    expect(screen.getByTestId("tab-agent-memory-error")).toBeInTheDocument();
  });

  it("renders short-term and long-term sections with counts and entries", () => {
    useMemoriesMock.mockReturnValue({
      data: {
        shortTerm: [
          {
            id: "s1",
            content: "remember the milk",
            createdAt: new Date().toISOString(),
            updatedAt: new Date().toISOString(),
            kind: "short_term",
            source: "ada",
            threadId: "abcdef0123456789",
          },
        ],
        longTerm: [
          {
            id: "l1",
            content: "Pi is roughly 3.14",
            createdAt: new Date().toISOString(),
            updatedAt: new Date().toISOString(),
            kind: "long_term",
            source: null,
          },
        ],
      },
      isLoading: false,
      error: null,
    });
    render(<MemoryTab kind="Unit" id="engineering" />);

    const root = screen.getByTestId("tab-unit-memory");
    expect(within(root).getByText("remember the milk")).toBeInTheDocument();
    expect(within(root).getByText("Pi is roughly 3.14")).toBeInTheDocument();
    expect(within(root).getByText("Short-term")).toBeInTheDocument();
    expect(within(root).getByText("Long-term")).toBeInTheDocument();
    expect(screen.getByTestId("tab-unit-memory-short-count")).toHaveTextContent(
      "1 entry",
    );
    expect(screen.getByTestId("tab-unit-memory-long-count")).toHaveTextContent(
      "1 entry",
    );
    expect(within(root).getByText(/source: ada/)).toBeInTheDocument();
    expect(within(root).getByText(/thread: abcdef01/)).toBeInTheDocument();
  });

  it("submits the search form with the typed query", () => {
    useMemoriesMock.mockReturnValue({
      data: { shortTerm: [], longTerm: [] },
      isLoading: false,
      error: null,
    });
    render(<MemoryTab kind="Agent" id="ada" />);

    fireEvent.change(screen.getByTestId("tab-agent-memory-search-input"), {
      target: { value: "react hooks" },
    });
    fireEvent.click(screen.getByTestId("tab-agent-memory-search-submit"));

    expect(useMemoriesMock).toHaveBeenLastCalledWith(
      "agent",
      "ada",
      expect.objectContaining({ query: "react hooks" }),
    );
  });

  it("clears the search query via the clear button", () => {
    useMemoriesMock.mockReturnValue({
      data: { shortTerm: [], longTerm: [] },
      isLoading: false,
      error: null,
    });
    render(<MemoryTab kind="Agent" id="ada" />);
    fireEvent.change(screen.getByTestId("tab-agent-memory-search-input"), {
      target: { value: "design" },
    });
    fireEvent.click(screen.getByTestId("tab-agent-memory-search-submit"));
    expect(useMemoriesMock).toHaveBeenLastCalledWith(
      "agent",
      "ada",
      expect.objectContaining({ query: "design" }),
    );

    fireEvent.click(screen.getByTestId("tab-agent-memory-search-clear"));
    expect(useMemoriesMock).toHaveBeenLastCalledWith(
      "agent",
      "ada",
      expect.objectContaining({ query: undefined }),
    );
  });

  it("advances the offset when Next is clicked and there is overflow", () => {
    // List-mode requests limit = PAGE_SIZE + 1 = 51 entries; the
    // component trims the overflow but enables `Next`. We seed exactly
    // one overflow entry on the long-term axis to flip hasNext = true.
    const longTerm = Array.from({ length: 51 }, (_, i) => ({
      id: `l${i}`,
      content: `entry ${i}`,
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString(),
      kind: "long_term",
      source: null,
    }));
    useMemoriesMock.mockReturnValue({
      data: { shortTerm: [], longTerm },
      isLoading: false,
      error: null,
    });
    render(<MemoryTab kind="Unit" id="engineering" />);

    const nextBtn = screen.getByTestId("tab-unit-memory-paging-next");
    expect(nextBtn).not.toBeDisabled();
    fireEvent.click(nextBtn);
    expect(useMemoriesMock).toHaveBeenLastCalledWith(
      "unit",
      "engineering",
      expect.objectContaining({ offset: 50 }),
    );
  });

  it("disables Previous when offset is zero", () => {
    useMemoriesMock.mockReturnValue({
      data: { shortTerm: [], longTerm: [] },
      isLoading: false,
      error: null,
    });
    render(<MemoryTab kind="Unit" id="engineering" />);
    expect(screen.getByTestId("tab-unit-memory-paging-prev")).toBeDisabled();
  });
});
