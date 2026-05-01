// Tests for the engagement list component (E2.4, #1416).

import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi, beforeEach } from "vitest";
import type { ReactNode } from "react";
import type { ThreadSummary } from "@/lib/api/types";

// ── mocks ──────────────────────────────────────────────────────────────────

vi.mock("next/link", () => ({
  default: ({
    href,
    children,
    ...rest
  }: { href: string; children: ReactNode } & Record<string, unknown>) => (
    <a href={href} {...rest}>
      {children}
    </a>
  ),
}));

const mockUseThreads = vi.fn();
const mockUseInbox = vi.fn();

vi.mock("@/lib/api/queries", () => ({
  useThreads: (...args: unknown[]) => mockUseThreads(...args),
  useInbox: (...args: unknown[]) => mockUseInbox(...args),
}));

// ── component import ───────────────────────────────────────────────────────

import { EngagementList } from "./engagement-list";

// ── helpers ───────────────────────────────────────────────────────────────

function makeThread(overrides: Partial<ThreadSummary> = {}): ThreadSummary {
  return {
    id: "thread-abc",
    participants: [
      { address: "human://savas", displayName: "savas" },
      { address: "agent://ada", displayName: "ada" },
    ],
    status: "active",
    lastActivity: new Date().toISOString(),
    createdAt: new Date().toISOString(),
    eventCount: 5,
    origin: { address: "human://savas", displayName: "savas" },
    summary: "Working on the feature",
    ...overrides,
  };
}

function idleQuery() {
  return { data: undefined, isPending: false, error: null, isFetching: false };
}

// ── tests ──────────────────────────────────────────────────────────────────

describe("EngagementList", () => {
  beforeEach(() => {
    mockUseInbox.mockReturnValue({ data: [], isPending: false, error: null });
  });

  describe("loading state", () => {
    it("shows a skeleton while data is pending", () => {
      mockUseThreads.mockReturnValue({
        data: undefined,
        isPending: true,
        error: null,
        isFetching: true,
      });

      render(<EngagementList slice="mine" />);
      expect(
        screen.getByTestId("engagement-list-loading"),
      ).toBeInTheDocument();
    });
  });

  describe("error state", () => {
    it("shows an error alert when the query fails", () => {
      mockUseThreads.mockReturnValue({
        data: undefined,
        isPending: false,
        error: new Error("Network error"),
        isFetching: false,
      });

      render(<EngagementList slice="mine" />);
      const alert = screen.getByTestId("engagement-list-error");
      expect(alert).toBeInTheDocument();
      expect(alert).toHaveAttribute("role", "alert");
      expect(screen.getByText(/Network error/)).toBeInTheDocument();
    });
  });

  describe("empty state", () => {
    it("shows the 'mine' empty state when no threads returned", () => {
      mockUseThreads.mockReturnValue({
        data: [],
        isPending: false,
        error: null,
        isFetching: false,
      });

      render(<EngagementList slice="mine" />);
      expect(
        screen.getByTestId("engagement-list-empty"),
      ).toBeInTheDocument();
      expect(
        screen.getByText("No engagements yet. Start a unit and assign it a task to begin an engagement."),
      ).toBeInTheDocument();
    });

    it("shows the 'unit' empty state when filtered by unit", () => {
      mockUseThreads.mockReturnValue({
        data: [],
        isPending: false,
        error: null,
        isFetching: false,
      });

      render(<EngagementList slice="unit" unit="engineering" />);
      expect(
        screen.getByText('No engagements found for unit "engineering".'),
      ).toBeInTheDocument();
    });

    it("shows the 'agent' empty state when filtered by agent", () => {
      mockUseThreads.mockReturnValue({
        data: [],
        isPending: false,
        error: null,
        isFetching: false,
      });

      render(<EngagementList slice="agent" agent="ada" />);
      expect(
        screen.getByText('No engagements found for agent "ada".'),
      ).toBeInTheDocument();
    });
  });

  describe("list rendering", () => {
    it("renders engagement cards", () => {
      mockUseThreads.mockReturnValue({
        data: [makeThread()],
        isPending: false,
        error: null,
        isFetching: false,
      });

      render(<EngagementList slice="mine" />);
      expect(screen.getByTestId("engagement-list")).toBeInTheDocument();
      expect(screen.getByTestId("engagement-card-thread-abc")).toBeInTheDocument();
    });

    it("card links to the engagement detail page", () => {
      mockUseThreads.mockReturnValue({
        data: [makeThread()],
        isPending: false,
        error: null,
        isFetching: false,
      });

      render(<EngagementList slice="mine" />);
      const card = screen.getByTestId("engagement-card-thread-abc");
      expect(card.closest("a")).toHaveAttribute("href", "/engagement/thread-abc");
    });

    it("excludes A2A-only threads from the 'mine' slice", () => {
      const a2aThread = makeThread({
        id: "thread-a2a",
        participants: [
          { address: "agent://ada", displayName: "ada" },
          { address: "agent://bob", displayName: "bob" },
        ],
      });
      const humanThread = makeThread({ id: "thread-human" });
      mockUseThreads.mockReturnValue({
        data: [a2aThread, humanThread],
        isPending: false,
        error: null,
        isFetching: false,
      });

      render(<EngagementList slice="mine" />);
      // A2A thread should not appear in "mine" slice
      expect(
        screen.queryByTestId("engagement-card-thread-a2a"),
      ).not.toBeInTheDocument();
      // Human thread should appear
      expect(
        screen.getByTestId("engagement-card-thread-human"),
      ).toBeInTheDocument();
    });

    it("includes A2A-only threads in the 'unit' slice", () => {
      const a2aThread = makeThread({
        id: "thread-a2a",
        participants: [
          { address: "agent://ada", displayName: "ada" },
          { address: "agent://bob", displayName: "bob" },
        ],
      });
      mockUseThreads.mockReturnValue({
        data: [a2aThread],
        isPending: false,
        error: null,
        isFetching: false,
      });

      render(<EngagementList slice="unit" unit="eng" />);
      expect(
        screen.getByTestId("engagement-card-thread-a2a"),
      ).toBeInTheDocument();
    });

    it("sorts threads by latest activity descending", () => {
      const older = makeThread({
        id: "thread-old",
        lastActivity: "2026-01-01T00:00:00Z",
      });
      const newer = makeThread({
        id: "thread-new",
        lastActivity: "2026-04-01T00:00:00Z",
      });
      // Return in wrong order (old first) — component should re-sort
      mockUseThreads.mockReturnValue({
        data: [older, newer],
        isPending: false,
        error: null,
        isFetching: false,
      });

      render(<EngagementList slice="mine" />);
      const cards = screen.getAllByTestId(/^engagement-card-thread-/);
      expect(cards[0]).toHaveAttribute("data-testid", "engagement-card-thread-new");
      expect(cards[1]).toHaveAttribute("data-testid", "engagement-card-thread-old");
    });

    it("shows a pending-question badge for inbox items matching the thread", () => {
      mockUseThreads.mockReturnValue({
        data: [makeThread({ id: "thread-q" })],
        isPending: false,
        error: null,
        isFetching: false,
      });
      mockUseInbox.mockReturnValue({
        data: [
          {
            threadId: "thread-q",
            from: "agent://ada",
            human: "human://savas",
            pendingSince: new Date().toISOString(),
            summary: "Which branch?",
          },
        ],
        isPending: false,
        error: null,
      });

      render(<EngagementList slice="mine" />);
      expect(screen.getByText("Question")).toBeInTheDocument();
    });

    it("does not show a pending-question badge when inbox is empty", () => {
      mockUseThreads.mockReturnValue({
        data: [makeThread()],
        isPending: false,
        error: null,
        isFetching: false,
      });
      mockUseInbox.mockReturnValue({
        data: [],
        isPending: false,
        error: null,
      });

      render(<EngagementList slice="mine" />);
      expect(screen.queryByText("Question")).not.toBeInTheDocument();
    });
  });

  describe("query params forwarding", () => {
    it("passes the unit filter when slice=unit", () => {
      mockUseThreads.mockReturnValue(idleQuery());
      render(<EngagementList slice="unit" unit="eng-team" />);
      expect(mockUseThreads).toHaveBeenCalledWith(
        expect.objectContaining({ unit: "eng-team" }),
        expect.any(Object),
      );
    });

    it("passes the agent filter when slice=agent", () => {
      mockUseThreads.mockReturnValue(idleQuery());
      render(<EngagementList slice="agent" agent="ada" />);
      expect(mockUseThreads).toHaveBeenCalledWith(
        expect.objectContaining({ agent: "ada" }),
        expect.any(Object),
      );
    });

    it("passes empty filters for the mine slice", () => {
      mockUseThreads.mockReturnValue(idleQuery());
      render(<EngagementList slice="mine" />);
      expect(mockUseThreads).toHaveBeenCalledWith(
        expect.objectContaining({}),
        expect.any(Object),
      );
    });
  });
});
