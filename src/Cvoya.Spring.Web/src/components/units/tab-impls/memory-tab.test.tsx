import { fireEvent, render, screen, within } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

const useMemoriesMock = vi.fn();
vi.mock("@/lib/api/queries", () => ({
  useMemories: (
    subject: string,
    id: string,
    options?: Record<string, unknown>,
  ) => useMemoriesMock(subject, id, options),
}));

import { MemoryTab } from "./memory-tab";

describe("MemoryTab", () => {
  beforeEach(() => {
    useMemoriesMock.mockReset();
  });

  it("calls useMemories with subject=unit when kind=Unit", () => {
    useMemoriesMock.mockReturnValue({
      data: { agent: [], thread: [] },
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

  it("calls useMemories with subject=agent when kind=Agent", () => {
    useMemoriesMock.mockReturnValue({
      data: { agent: [], thread: [] },
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

  it("renders agent-scoped and thread-scoped sections with counts and entries", () => {
    useMemoriesMock.mockReturnValue({
      data: {
        agent: [
          {
            id: "l1",
            content: "Pi is roughly 3.14",
            createdAt: new Date().toISOString(),
            updatedAt: new Date().toISOString(),
            scope: "agent",
            source: null,
          },
        ],
        thread: [
          {
            id: "s1",
            content: "remember the milk",
            createdAt: new Date().toISOString(),
            updatedAt: new Date().toISOString(),
            scope: "thread",
            source: "ada",
            threadId: "abcdef0123456789",
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
    expect(within(root).getByText("Agent-scoped")).toBeInTheDocument();
    expect(within(root).getByText("Thread-scoped")).toBeInTheDocument();
    expect(screen.getByTestId("tab-unit-memory-agent-count")).toHaveTextContent(
      "1 entry",
    );
    expect(
      screen.getByTestId("tab-unit-memory-thread-count"),
    ).toHaveTextContent("1 entry");
    expect(within(root).getByText(/source: ada/)).toBeInTheDocument();
    expect(within(root).getByText(/thread: abcdef01/)).toBeInTheDocument();
  });

  it("renders structured JSON content as formatted JSON, not [object Object]", () => {
    useMemoriesMock.mockReturnValue({
      data: {
        agent: [
          {
            id: "j1",
            content: { status: "published", piece: 3 },
            createdAt: new Date().toISOString(),
            updatedAt: new Date().toISOString(),
            scope: "agent",
            source: null,
          },
        ],
        thread: [],
      },
      isLoading: false,
      error: null,
    });
    render(<MemoryTab kind="Agent" id="ada" />);

    const root = screen.getByTestId("tab-agent-memory");
    expect(within(root).queryByText("[object Object]")).not.toBeInTheDocument();
    const item = within(root).getByTestId("tab-agent-memory-agent-item-j1");
    expect(item.textContent).toContain('"status": "published"');
    expect(item.textContent).toContain('"piece": 3');
  });

  it("submits the search form with the typed query", () => {
    useMemoriesMock.mockReturnValue({
      data: { agent: [], thread: [] },
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
      data: { agent: [], thread: [] },
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
    // one overflow entry on the agent-scoped side to flip hasNext = true.
    const agent = Array.from({ length: 51 }, (_, i) => ({
      id: `l${i}`,
      content: `entry ${i}`,
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString(),
      scope: "agent",
      source: null,
    }));
    useMemoriesMock.mockReturnValue({
      data: { agent, thread: [] },
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
      data: { agent: [], thread: [] },
      isLoading: false,
      error: null,
    });
    render(<MemoryTab kind="Unit" id="engineering" />);
    expect(screen.getByTestId("tab-unit-memory-paging-prev")).toBeDisabled();
  });
});
