// Tests for the engagement list component.

import { render, screen, fireEvent } from "@testing-library/react";
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
const mockUseCurrentUser = vi.fn();

vi.mock("@/lib/api/queries", () => ({
  useThreads: (...args: unknown[]) => mockUseThreads(...args),
  useInbox: (...args: unknown[]) => mockUseInbox(...args),
  useCurrentUser: (...args: unknown[]) => mockUseCurrentUser(...args),
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

const CURRENT_USER = {
  userId: "savas",
  displayName: "savas",
  address: "human://savas",
};

// ── tests ──────────────────────────────────────────────────────────────────

describe("EngagementList", () => {
  beforeEach(() => {
    mockUseInbox.mockReturnValue({ data: [], isPending: false, error: null });
    mockUseCurrentUser.mockReturnValue({
      data: CURRENT_USER,
      isPending: false,
      error: null,
    });
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
        screen.getByText(
          "No engagements yet. Start a unit and assign it a task to begin an engagement.",
        ),
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
      expect(
        screen.getByTestId("engagement-card-thread-abc"),
      ).toBeInTheDocument();
    });

    it("renders the card title from participant display names, excluding the current user", () => {
      mockUseThreads.mockReturnValue({
        data: [
          makeThread({
            participants: [
              { address: "human://savas", displayName: "savas" },
              { address: "agent://ada", displayName: "ada" },
              { address: "agent://bob", displayName: "bob" },
            ],
          }),
        ],
        isPending: false,
        error: null,
        isFetching: false,
      });

      render(<EngagementList slice="mine" />);
      const title = screen.getByTestId("engagement-card-title");
      expect(title).toHaveTextContent("ada, bob");
      expect(title).not.toHaveTextContent("savas");
      expect(title).not.toHaveTextContent("thread-abc");
    });

    it("uses an ellipsis when the participant list is long", () => {
      mockUseThreads.mockReturnValue({
        data: [
          makeThread({
            participants: [
              { address: "human://savas", displayName: "savas" },
              { address: "agent://ada", displayName: "ada" },
              { address: "agent://bob", displayName: "bob" },
              { address: "agent://carl", displayName: "carl" },
              { address: "agent://dot", displayName: "dot" },
            ],
          }),
        ],
        isPending: false,
        error: null,
        isFetching: false,
      });

      render(<EngagementList slice="mine" />);
      const title = screen.getByTestId("engagement-card-title");
      expect(title.textContent).toMatch(/ada, bob, carl, …$/);
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
      expect(card.closest("a")).toHaveAttribute(
        "href",
        "/engagement/thread-abc",
      );
    });

    it("highlights the selected thread", () => {
      mockUseThreads.mockReturnValue({
        data: [
          makeThread({ id: "thread-1" }),
          makeThread({ id: "thread-2" }),
        ],
        isPending: false,
        error: null,
        isFetching: false,
      });

      render(<EngagementList slice="mine" selectedThreadId="thread-2" />);
      const selected = screen.getByTestId("engagement-card-thread-2");
      expect(selected).toHaveAttribute("aria-current", "page");
      const other = screen.getByTestId("engagement-card-thread-1");
      expect(other).not.toHaveAttribute("aria-current");
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
      mockUseThreads.mockReturnValue({
        data: [older, newer],
        isPending: false,
        error: null,
        isFetching: false,
      });

      render(<EngagementList slice="mine" />);
      const cards = screen.getAllByTestId(/^engagement-card-thread-/);
      expect(cards[0]).toHaveAttribute(
        "data-testid",
        "engagement-card-thread-new",
      );
      expect(cards[1]).toHaveAttribute(
        "data-testid",
        "engagement-card-thread-old",
      );
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
            from: { address: "agent://ada", displayName: "ada" },
            human: { address: "human://savas", displayName: "savas" },
            pendingSince: new Date().toISOString(),
            summary: "Which branch?",
            unreadCount: 1,
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

  describe("visibility filter", () => {
    it("renders the filter dropdown by default", () => {
      mockUseThreads.mockReturnValue({
        data: [makeThread()],
        isPending: false,
        error: null,
        isFetching: false,
      });

      render(<EngagementList slice="mine" />);
      expect(
        screen.getByTestId("engagement-filter-trigger"),
      ).toBeInTheDocument();
      expect(screen.getByTestId("engagement-filter-label")).toHaveTextContent(
        "All",
      );
    });

    it("hides the filter dropdown when hideFilter is set", () => {
      mockUseThreads.mockReturnValue({
        data: [makeThread()],
        isPending: false,
        error: null,
        isFetching: false,
      });

      render(<EngagementList slice="mine" hideFilter />);
      expect(
        screen.queryByTestId("engagement-filter-trigger"),
      ).not.toBeInTheDocument();
    });

    it("filters to participant-only when 'Participant' is selected", () => {
      const participantThread = makeThread({
        id: "thread-mine",
        participants: [
          { address: "human://savas", displayName: "savas" },
          { address: "agent://ada", displayName: "ada" },
        ],
      });
      const observerThread = makeThread({
        id: "thread-a2a",
        participants: [
          { address: "agent://ada", displayName: "ada" },
          { address: "agent://bob", displayName: "bob" },
        ],
      });
      mockUseThreads.mockReturnValue({
        data: [participantThread, observerThread],
        isPending: false,
        error: null,
        isFetching: false,
      });

      render(<EngagementList slice="mine" />);
      // Both threads visible under "All".
      expect(
        screen.getByTestId("engagement-card-thread-mine"),
      ).toBeInTheDocument();
      expect(
        screen.getByTestId("engagement-card-thread-a2a"),
      ).toBeInTheDocument();

      fireEvent.click(screen.getByTestId("engagement-filter-trigger"));
      fireEvent.click(
        screen.getByTestId("engagement-filter-option-participant"),
      );

      expect(
        screen.getByTestId("engagement-card-thread-mine"),
      ).toBeInTheDocument();
      expect(
        screen.queryByTestId("engagement-card-thread-a2a"),
      ).not.toBeInTheDocument();
    });

    it("filters to observer-only when 'Observer' is selected", () => {
      const participantThread = makeThread({
        id: "thread-mine",
        participants: [
          { address: "human://savas", displayName: "savas" },
          { address: "agent://ada", displayName: "ada" },
        ],
      });
      const observerThread = makeThread({
        id: "thread-a2a",
        participants: [
          { address: "agent://ada", displayName: "ada" },
          { address: "agent://bob", displayName: "bob" },
        ],
      });
      mockUseThreads.mockReturnValue({
        data: [participantThread, observerThread],
        isPending: false,
        error: null,
        isFetching: false,
      });

      render(<EngagementList slice="mine" />);
      fireEvent.click(screen.getByTestId("engagement-filter-trigger"));
      fireEvent.click(
        screen.getByTestId("engagement-filter-option-observer"),
      );

      expect(
        screen.queryByTestId("engagement-card-thread-mine"),
      ).not.toBeInTheDocument();
      expect(
        screen.getByTestId("engagement-card-thread-a2a"),
      ).toBeInTheDocument();
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
