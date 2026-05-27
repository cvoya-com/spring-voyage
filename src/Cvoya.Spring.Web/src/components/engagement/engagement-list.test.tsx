// Tests for the engagement list component.

import { render, screen, fireEvent } from "@testing-library/react";
import { describe, expect, it, vi, beforeEach } from "vitest";
import type { ReactNode } from "react";
import type { ThreadSummary, ThreadListFilters } from "@/lib/api/types";

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

// `useThreads` is called twice per <EngagementList> render: once for the
// active slice (no `archived` flag) and once for the archived slice
// (`archived: true`). The mock dispatches to two separate `vi.fn` handles
// so tests can wire each slice independently — and the existing tests
// (which only stub the active slice) keep working since the archived
// slice falls back to the "idle empty" reply.
const mockUseThreads = vi.fn();
const mockUseArchivedThreads = vi.fn();
const mockUseInbox = vi.fn();
const mockUseCurrentUser = vi.fn();

function dispatchUseThreads(...args: unknown[]) {
  const filters = args[0] as ThreadListFilters | undefined;
  if (filters?.archived === true) {
    return mockUseArchivedThreads(...args);
  }
  return mockUseThreads(...args);
}

vi.mock("@/lib/api/queries", () => ({
  useThreads: (...args: unknown[]) => dispatchUseThreads(...args),
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
      { id: "11111111-1111-1111-1111-111111111111", address: "human://savas", displayName: "savas" },
      { id: "22222222-2222-2222-2222-222222222222", address: "agent://ada", displayName: "ada" },
    ],
    lastActivity: new Date().toISOString(),
    createdAt: new Date().toISOString(),
    eventCount: 5,
    origin: { id: "11111111-1111-1111-1111-111111111111", address: "human://savas", displayName: "savas" },
    summary: "Working on the feature",
    // #2732: derived auto-archive flag. Default fixtures stay live;
    // tests that exercise the archive surface override via the spread.
    isArchived: false,
    ...overrides,
  };
}

function idleQuery() {
  return { data: undefined, isPending: false, error: null, isFetching: false };
}

const CURRENT_USER = {
  userId: "savas",
  displayName: "savas",
  // #2082: identity comparisons go via Guid `id`. The textual `address`
  // stays for display / "from" routing but is no longer compared as a
  // primitive.
  id: "11111111-1111-1111-1111-111111111111",
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
    // Default the archived slice to "idle empty". Individual tests
    // exercising the archived section override this explicitly.
    mockUseArchivedThreads.mockReturnValue({
      data: [],
      isPending: false,
      error: null,
      isFetching: false,
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
      expect(
        screen.getByTestId("engagement-list-error"),
      ).toBeInTheDocument();
      // `<ApiErrorMessage>` renders the friendly fallback for non-
      // ProblemDetails errors and carries `role="alert"` itself (#2163).
      expect(screen.getByRole("alert")).toBeInTheDocument();
      expect(screen.getByText("Something went wrong.")).toBeInTheDocument();
      expect(screen.queryByText(/API error/)).not.toBeInTheDocument();
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
              { id: "11111111-1111-1111-1111-111111111111", address: "human://savas", displayName: "savas" },
              { id: "22222222-2222-2222-2222-222222222222", address: "agent://ada", displayName: "ada" },
              { id: "33333333-3333-3333-3333-333333333333", address: "agent://bob", displayName: "bob" },
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

    // #1630
    it("never leaks raw GUIDs into the title when displayName is missing", () => {
      const id = "d4ce4258-ab40-4c10-be06-407cc5ec9139";
      mockUseThreads.mockReturnValue({
        data: [
          makeThread({
            id: "thread-guidy",
            participants: [
              { id: "11111111-1111-1111-1111-111111111111", address: "human://savas", displayName: "savas" },
              // identity form, no displayName from the server — exactly
              // the wire shape that produced the GUID-titled cards in
              // the issue screenshot.
              { id, address: `agent:id:${id}`, displayName: "" },
            ],
          }),
        ],
        isPending: false,
        error: null,
        isFetching: false,
      });

      render(<EngagementList slice="mine" />);
      const title = screen.getByTestId("engagement-card-title");
      // Falls back to a soft placeholder rather than dumping the GUID.
      expect(title.textContent).not.toMatch(id);
      expect(title.textContent).not.toMatch(/agent:id:/);
    });

    // #1630
    it("renders all participant names for engagements where the user is an observer", () => {
      mockUseThreads.mockReturnValue({
        data: [
          makeThread({
            id: "thread-observed",
            // No human in the participant list — the active user is
            // observing, not a participant. The title should list every
            // participant rather than hiding behind "Just you".
            participants: [
              { id: "22222222-2222-2222-2222-222222222222", address: "agent://ada", displayName: "ada" },
              { id: "33333333-3333-3333-3333-333333333333", address: "agent://bob", displayName: "bob" },
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
    });

    it("uses an ellipsis when the participant list is long", () => {
      mockUseThreads.mockReturnValue({
        data: [
          makeThread({
            participants: [
              { id: "11111111-1111-1111-1111-111111111111", address: "human://savas", displayName: "savas" },
              { id: "22222222-2222-2222-2222-222222222222", address: "agent://ada", displayName: "ada" },
              { id: "33333333-3333-3333-3333-333333333333", address: "agent://bob", displayName: "bob" },
              { id: "44444444-4444-4444-4444-444444444444", address: "agent://carl", displayName: "carl" },
              { id: "55555555-5555-5555-5555-555555555555", address: "agent://dot", displayName: "dot" },
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
            from: { id: "22222222-2222-2222-2222-222222222222", address: "agent://ada", displayName: "ada" },
            human: { id: "11111111-1111-1111-1111-111111111111", address: "human://savas", displayName: "savas" },
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
          { id: "11111111-1111-1111-1111-111111111111", address: "human://savas", displayName: "savas" },
          { id: "22222222-2222-2222-2222-222222222222", address: "agent://ada", displayName: "ada" },
        ],
      });
      const observerThread = makeThread({
        id: "thread-a2a",
        participants: [
          { id: "22222222-2222-2222-2222-222222222222", address: "agent://ada", displayName: "ada" },
          { id: "33333333-3333-3333-3333-333333333333", address: "agent://bob", displayName: "bob" },
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
          { id: "11111111-1111-1111-1111-111111111111", address: "human://savas", displayName: "savas" },
          { id: "22222222-2222-2222-2222-222222222222", address: "agent://ada", displayName: "ada" },
        ],
      });
      const observerThread = makeThread({
        id: "thread-a2a",
        participants: [
          { id: "22222222-2222-2222-2222-222222222222", address: "agent://ada", displayName: "ada" },
          { id: "33333333-3333-3333-3333-333333333333", address: "agent://bob", displayName: "bob" },
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

    // #2732: the archived slice mirrors the active slice's scope so the
    // archived list is restricted to the same unit / agent. The active
    // call must *not* carry `archived: true` (server default omits
    // archived; we keep the URL identical to the pre-archive shape).
    it("fetches the archived slice with the same scope and archived=true", () => {
      mockUseThreads.mockReturnValue(idleQuery());
      render(<EngagementList slice="unit" unit="eng-team" />);
      expect(mockUseArchivedThreads).toHaveBeenCalledWith(
        expect.objectContaining({ unit: "eng-team", archived: true }),
        expect.any(Object),
      );
      // The active call does not opt into the archived flag — that
      // would invert the response.
      expect(mockUseThreads).not.toHaveBeenCalledWith(
        expect.objectContaining({ archived: true }),
        expect.any(Object),
      );
    });
  });

  // ── archived section (#2732) ─────────────────────────────────────────────

  describe("archived section", () => {
    function makeArchived(overrides: Partial<ThreadSummary> = {}) {
      return makeThread({ isArchived: true, ...overrides });
    }

    it("does not render the archived section when archived count is 0", () => {
      mockUseThreads.mockReturnValue({
        data: [makeThread()],
        isPending: false,
        error: null,
        isFetching: false,
      });
      // archived defaults to [] via beforeEach
      render(<EngagementList slice="mine" />);
      expect(
        screen.queryByTestId("engagement-archived-section"),
      ).not.toBeInTheDocument();
      expect(
        screen.queryByTestId("engagement-archived-toggle"),
      ).not.toBeInTheDocument();
    });

    it("renders the archived header with the count when archived threads exist", () => {
      mockUseThreads.mockReturnValue({
        data: [makeThread({ id: "thread-live" })],
        isPending: false,
        error: null,
        isFetching: false,
      });
      mockUseArchivedThreads.mockReturnValue({
        data: [
          makeArchived({ id: "thread-arc-a" }),
          makeArchived({ id: "thread-arc-b" }),
        ],
        isPending: false,
        error: null,
        isFetching: false,
      });

      render(<EngagementList slice="mine" />);
      const toggle = screen.getByTestId("engagement-archived-toggle");
      expect(toggle).toBeInTheDocument();
      expect(toggle).toHaveAttribute("aria-expanded", "false");
      // ARIA label conveys count for screen readers as a real text node.
      expect(toggle).toHaveAttribute("aria-label", "Archived, 2 items");
      expect(
        screen.getByTestId("engagement-archived-count"),
      ).toHaveTextContent("(2)");
    });

    it("uses the singular ARIA-label form when archived count is 1", () => {
      mockUseThreads.mockReturnValue({
        data: [makeThread()],
        isPending: false,
        error: null,
        isFetching: false,
      });
      mockUseArchivedThreads.mockReturnValue({
        data: [makeArchived({ id: "thread-arc-solo" })],
        isPending: false,
        error: null,
        isFetching: false,
      });

      render(<EngagementList slice="mine" />);
      expect(
        screen.getByTestId("engagement-archived-toggle"),
      ).toHaveAttribute("aria-label", "Archived, 1 item");
    });

    it("starts collapsed and expands on click", () => {
      mockUseThreads.mockReturnValue({
        data: [makeThread()],
        isPending: false,
        error: null,
        isFetching: false,
      });
      mockUseArchivedThreads.mockReturnValue({
        data: [makeArchived({ id: "thread-arc-a" })],
        isPending: false,
        error: null,
        isFetching: false,
      });

      render(<EngagementList slice="mine" />);
      // Collapsed by default — archived card not in DOM.
      expect(
        screen.queryByTestId("engagement-card-thread-arc-a"),
      ).not.toBeInTheDocument();

      fireEvent.click(screen.getByTestId("engagement-archived-toggle"));

      const toggle = screen.getByTestId("engagement-archived-toggle");
      expect(toggle).toHaveAttribute("aria-expanded", "true");
      // aria-controls points to the panel that just rendered.
      expect(toggle).toHaveAttribute(
        "aria-controls",
        "engagement-archived-list",
      );
      expect(screen.getByTestId("engagement-archived-list")).toHaveAttribute(
        "id",
        "engagement-archived-list",
      );
      expect(
        screen.getByTestId("engagement-card-thread-arc-a"),
      ).toBeInTheDocument();
    });

    it("renders the archived section even when the active list is empty", () => {
      mockUseThreads.mockReturnValue({
        data: [],
        isPending: false,
        error: null,
        isFetching: false,
      });
      mockUseArchivedThreads.mockReturnValue({
        data: [makeArchived({ id: "thread-arc-solo" })],
        isPending: false,
        error: null,
        isFetching: false,
      });

      render(<EngagementList slice="mine" />);
      // The "No engagements" empty-state must not render when archived
      // threads are available — that would be a confusing dead-end.
      expect(
        screen.queryByTestId("engagement-list-empty"),
      ).not.toBeInTheDocument();
      expect(
        screen.getByTestId("engagement-archived-section"),
      ).toBeInTheDocument();
    });

    it("renders the empty state when BOTH active and archived are empty", () => {
      mockUseThreads.mockReturnValue({
        data: [],
        isPending: false,
        error: null,
        isFetching: false,
      });
      mockUseArchivedThreads.mockReturnValue({
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
        screen.queryByTestId("engagement-archived-section"),
      ).not.toBeInTheDocument();
    });

    it("renders the active list immediately when the archived query is still loading", () => {
      mockUseThreads.mockReturnValue({
        data: [makeThread({ id: "thread-live" })],
        isPending: false,
        error: null,
        isFetching: false,
      });
      mockUseArchivedThreads.mockReturnValue({
        // archived still loading — must not block the active list.
        data: undefined,
        isPending: true,
        error: null,
        isFetching: true,
      });

      render(<EngagementList slice="mine" />);
      expect(
        screen.getByTestId("engagement-card-thread-live"),
      ).toBeInTheDocument();
      // No archived section yet — we don't render a header while the
      // count is unknown (0).
      expect(
        screen.queryByTestId("engagement-archived-section"),
      ).not.toBeInTheDocument();
    });

    it("does not block the active list when the archived query errors", () => {
      mockUseThreads.mockReturnValue({
        data: [makeThread({ id: "thread-live" })],
        isPending: false,
        error: null,
        isFetching: false,
      });
      mockUseArchivedThreads.mockReturnValue({
        data: undefined,
        isPending: false,
        error: new Error("archived fetch failed"),
        isFetching: false,
      });

      render(<EngagementList slice="mine" />);
      expect(
        screen.getByTestId("engagement-card-thread-live"),
      ).toBeInTheDocument();
      // The archived error is silent — the active list is the
      // critical path. Failure surface (#2732 follow-up) is out of
      // scope for the v0.1 archived UX.
      expect(
        screen.queryByTestId("engagement-archived-section"),
      ).not.toBeInTheDocument();
    });

    it("applies the visibility filter independently to the archived section", () => {
      mockUseThreads.mockReturnValue({
        data: [],
        isPending: false,
        error: null,
        isFetching: false,
      });
      mockUseArchivedThreads.mockReturnValue({
        data: [
          // Participated archived thread (human is in the list).
          makeArchived({
            id: "thread-arc-mine",
            participants: [
              {
                id: "11111111-1111-1111-1111-111111111111",
                address: "human://savas",
                displayName: "savas",
              },
              {
                id: "22222222-2222-2222-2222-222222222222",
                address: "agent://ada",
                displayName: "ada",
              },
            ],
          }),
          // Observer-only archived thread (A2A pair, no human).
          makeArchived({
            id: "thread-arc-a2a",
            participants: [
              {
                id: "22222222-2222-2222-2222-222222222222",
                address: "agent://ada",
                displayName: "ada",
              },
              {
                id: "33333333-3333-3333-3333-333333333333",
                address: "agent://bob",
                displayName: "bob",
              },
            ],
          }),
        ],
        isPending: false,
        error: null,
        isFetching: false,
      });

      render(<EngagementList slice="mine" />);
      // Both visible under "All". Header shows (2). Expand to verify.
      expect(
        screen.getByTestId("engagement-archived-count"),
      ).toHaveTextContent("(2)");
      fireEvent.click(screen.getByTestId("engagement-archived-toggle"));
      expect(
        screen.getByTestId("engagement-card-thread-arc-mine"),
      ).toBeInTheDocument();
      expect(
        screen.getByTestId("engagement-card-thread-arc-a2a"),
      ).toBeInTheDocument();

      // Switch to participant-only. The header count drops to 1 and
      // the observer-only archived thread disappears.
      fireEvent.click(screen.getByTestId("engagement-filter-trigger"));
      fireEvent.click(
        screen.getByTestId("engagement-filter-option-participant"),
      );
      expect(
        screen.getByTestId("engagement-archived-count"),
      ).toHaveTextContent("(1)");
      expect(
        screen.getByTestId("engagement-card-thread-arc-mine"),
      ).toBeInTheDocument();
      expect(
        screen.queryByTestId("engagement-card-thread-arc-a2a"),
      ).not.toBeInTheDocument();
    });

    it("muted styling — archived list container carries opacity-70", () => {
      mockUseThreads.mockReturnValue({
        data: [makeThread()],
        isPending: false,
        error: null,
        isFetching: false,
      });
      mockUseArchivedThreads.mockReturnValue({
        data: [makeArchived({ id: "thread-arc-a" })],
        isPending: false,
        error: null,
        isFetching: false,
      });

      render(<EngagementList slice="mine" />);
      fireEvent.click(screen.getByTestId("engagement-archived-toggle"));
      const panel = screen.getByTestId("engagement-archived-list");
      expect(panel.className).toContain("opacity-70");
    });
  });

  // ── Hat chip (ADR-0062 § 5, #2826) ───────────────────────────────────────

  describe("per-row Hat chip (ADR-0062 § 5)", () => {
    it("renders the chip with the receiving Hat's display name when the wire field is set", () => {
      mockUseThreads.mockReturnValue({
        data: [
          makeThread({
            id: "thread-hat",
            recipientHumanId: "33333333-3333-3333-3333-333333333333",
            recipientHumanDisplayName: "savas",
          }),
        ],
        isPending: false,
        error: null,
        isFetching: false,
      });

      render(<EngagementList slice="mine" />);
      const chip = screen.getByTestId("engagement-hat-chip-thread-hat");
      expect(chip).toHaveTextContent("As savas");
      expect(chip).toHaveAttribute("title", "Received as savas");
    });

    it("hides the chip when recipientHumanDisplayName is null (pure A2A thread)", () => {
      mockUseThreads.mockReturnValue({
        data: [
          makeThread({
            id: "thread-a2a",
            recipientHumanId: null,
            recipientHumanDisplayName: null,
          }),
        ],
        isPending: false,
        error: null,
        isFetching: false,
      });

      render(<EngagementList slice="mine" />);
      expect(
        screen.queryByTestId("engagement-hat-chip-thread-a2a"),
      ).not.toBeInTheDocument();
    });

    it("renders the chip on the sidebar variant as well as the page variant", () => {
      mockUseThreads.mockReturnValue({
        data: [
          makeThread({
            id: "thread-sidebar",
            recipientHumanDisplayName: "ada",
          }),
        ],
        isPending: false,
        error: null,
        isFetching: false,
      });

      render(<EngagementList slice="mine" variant="sidebar" />);
      const chip = screen.getByTestId("engagement-hat-chip-thread-sidebar");
      expect(chip).toHaveTextContent("As ada");
    });

    // ── #2829: disambiguated label ──────────────────────────────────────────
    it("renders the server-supplied disambiguated label when present (#2829)", () => {
      mockUseThreads.mockReturnValue({
        data: [
          makeThread({
            id: "thread-disambig",
            recipientHumanId: "44444444-4444-4444-4444-444444444444",
            recipientHumanDisplayName: "Bob",
            recipientHumanDisambiguatedLabel: "Bob — designer",
          }),
        ],
        isPending: false,
        error: null,
        isFetching: false,
      });

      render(<EngagementList slice="mine" />);
      const chip = screen.getByTestId("engagement-hat-chip-thread-disambig");
      expect(chip).toHaveTextContent("As Bob — designer");
      expect(chip).toHaveAttribute("title", "Received as Bob — designer");
    });

    it("falls back to the raw display name when the disambiguated label is null (#2829)", () => {
      mockUseThreads.mockReturnValue({
        data: [
          makeThread({
            id: "thread-fallback",
            recipientHumanId: "55555555-5555-5555-5555-555555555555",
            recipientHumanDisplayName: "Carol",
            recipientHumanDisambiguatedLabel: null,
          }),
        ],
        isPending: false,
        error: null,
        isFetching: false,
      });

      render(<EngagementList slice="mine" />);
      const chip = screen.getByTestId("engagement-hat-chip-thread-fallback");
      expect(chip).toHaveTextContent("As Carol");
    });
  });
});
