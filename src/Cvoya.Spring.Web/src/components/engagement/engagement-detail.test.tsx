// Tests for the engagement detail component (E2.5 + E2.6, #1417, #1418).

import { render, screen, fireEvent } from "@testing-library/react";
import { describe, expect, it, vi, beforeEach } from "vitest";
import type { ReactNode } from "react";
import type { ThreadDetail, InboxItem } from "@/lib/api/types";

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

const mockUseThread = vi.fn();
const mockUseCurrentUser = vi.fn();
const mockUseCallerHumans = vi.fn();
const mockUseInbox = vi.fn();

vi.mock("@/lib/api/queries", () => ({
  useThread: (...args: unknown[]) => mockUseThread(...args),
  useCurrentUser: (...args: unknown[]) => mockUseCurrentUser(...args),
  useCallerHumans: (...args: unknown[]) => mockUseCallerHumans(...args),
  useInbox: (...args: unknown[]) => mockUseInbox(...args),
}));

// Mock child components to isolate the detail logic.
vi.mock("./engagement-timeline", () => ({
  EngagementTimeline: ({
    threadId,
    layout,
  }: {
    threadId: string;
    layout?: "dialog" | "timeline";
  }) => (
    <div
      data-testid="mock-timeline"
      data-thread-id={threadId}
      data-layout={layout ?? "dialog"}
    />
  ),
}));

vi.mock("./engagement-composer", () => ({
  EngagementComposer: ({
    threadId,
    initialKind,
    onSendSuccess,
  }: {
    threadId: string;
    participants?: string[];
    initialKind?: string;
    onSendSuccess?: () => void;
  }) => (
    <div
      data-testid="mock-composer"
      data-thread-id={threadId}
      data-kind={initialKind}
    >
      <button
        data-testid="mock-send-success"
        onClick={() => onSendSuccess?.()}
        type="button"
      >
        Trigger send success
      </button>
    </div>
  ),
}));

// ── component import ───────────────────────────────────────────────────────

import { EngagementDetail } from "./engagement-detail";

// ── helpers ───────────────────────────────────────────────────────────────

function makeThread(overrides: Partial<ThreadDetail["summary"]> = {}): ThreadDetail {
  return {
    summary: {
      id: "thread-abc",
      participants: [
        { id: "11111111-1111-1111-1111-111111111111", address: "human://savas", displayName: "savas" },
        { id: "22222222-2222-2222-2222-222222222222", address: "agent://ada", displayName: "ada" },
      ],
      lastActivity: new Date().toISOString(),
      createdAt: new Date().toISOString(),
      eventCount: 3,
      origin: { id: "11111111-1111-1111-1111-111111111111", address: "human://savas", displayName: "savas" },
      summary: "Test engagement",
      // #2732: derived auto-archive flag. Default fixtures stay live;
      // tests that exercise the archive surface override via the spread.
      isArchived: false,
      ...overrides,
    },
    events: [],
  };
}

function makeInboxItem(overrides: Partial<InboxItem> = {}): InboxItem {
  return {
    threadId: "thread-abc",
    from: { id: "22222222-2222-2222-2222-222222222222", address: "agent://ada", displayName: "ada" },
    human: { id: "11111111-1111-1111-1111-111111111111", address: "human://savas", displayName: "savas" },
    pendingSince: new Date().toISOString(),
    summary: "Which branch?",
    unreadCount: 0,
    ...overrides,
  };
}

// #2082: identity comparisons go via Guid `id`; the textual address is
// retained for display / "from" routing but no longer drives "am I a
// participant?".
const CURRENT_USER = {
  id: "11111111-1111-1111-1111-111111111111",
  address: "human://savas",
};

// #2888: an operator wears one Hat per unit (ADR-0062). The Hat that
// `/auth/me` resolves (the auth-username Hat) and the unit-scoped Hat
// stamped on a thread are different GUIDs. `AUTH_USERNAME_HAT` is the
// `/auth/me` identity; `UNIT_SENDING_HAT_ID` is the "savas" participant
// in `makeThread()` — i.e. the Hat the operator actually sent into the
// thread with.
const AUTH_USERNAME_HAT = {
  id: "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
  address: "human://savas-auth",
};
const UNIT_SENDING_HAT_ID = "11111111-1111-1111-1111-111111111111";

// ── tests ──────────────────────────────────────────────────────────────────

describe("EngagementDetail", () => {
  beforeEach(() => {
    mockUseInbox.mockReturnValue({ data: [], isPending: false, error: null });
    mockUseCurrentUser.mockReturnValue({
      data: CURRENT_USER,
      isPending: false,
      error: null,
    });
    // #2888: the bound-Hat set is empty by default, so the existing
    // single-Hat tests classify the operator via the `/me.id` floor
    // (CURRENT_USER.id is the "savas" participant). The multi-Hat suite
    // below supplies a bound set whose member is the sending participant
    // while `/me.id` is a different Hat.
    mockUseCallerHumans.mockReturnValue({
      data: [],
      isPending: false,
      error: null,
    });
  });

  describe("loading state", () => {
    it("shows a skeleton while thread is loading", () => {
      mockUseThread.mockReturnValue({
        data: undefined,
        isPending: true,
        error: null,
      });

      render(<EngagementDetail threadId="thread-abc" />);
      expect(
        screen.getByTestId("engagement-detail-loading"),
      ).toBeInTheDocument();
    });

    it("shows a skeleton while user is loading", () => {
      mockUseThread.mockReturnValue({
        data: makeThread(),
        isPending: false,
        error: null,
      });
      mockUseCurrentUser.mockReturnValue({
        data: undefined,
        isPending: true,
        error: null,
      });

      render(<EngagementDetail threadId="thread-abc" />);
      expect(
        screen.getByTestId("engagement-detail-loading"),
      ).toBeInTheDocument();
    });

    // #2888: participant classification reads the caller's bound-Hat set,
    // so the view must wait for it too — otherwise a unit-Hat thread would
    // flash the observe banner before the bound set arrives and flips it
    // to the composer.
    it("shows a skeleton while the caller's Hats are loading", () => {
      mockUseThread.mockReturnValue({
        data: makeThread(),
        isPending: false,
        error: null,
      });
      mockUseCallerHumans.mockReturnValue({
        data: undefined,
        isPending: true,
        error: null,
      });

      render(<EngagementDetail threadId="thread-abc" />);
      expect(
        screen.getByTestId("engagement-detail-loading"),
      ).toBeInTheDocument();
    });
  });

  describe("error state", () => {
    it("shows an error alert when the thread query fails", () => {
      mockUseThread.mockReturnValue({
        data: undefined,
        isPending: false,
        error: new Error("Not found"),
      });

      render(<EngagementDetail threadId="thread-abc" />);
      expect(
        screen.getByTestId("engagement-detail-error"),
      ).toBeInTheDocument();
      // `<ApiErrorMessage>` renders the friendly fallback copy with
      // its own `role="alert"` on the inner alert box (#2163).
      expect(screen.getByRole("alert")).toBeInTheDocument();
      expect(screen.getByText("Something went wrong.")).toBeInTheDocument();
      expect(screen.queryByText(/API error/)).not.toBeInTheDocument();
    });
  });

  describe("not-found state", () => {
    it("shows a not-found message when thread data is null", () => {
      mockUseThread.mockReturnValue({
        data: null,
        isPending: false,
        error: null,
      });

      render(<EngagementDetail threadId="thread-abc" />);
      expect(
        screen.getByTestId("engagement-detail-not-found"),
      ).toBeInTheDocument();
    });
  });

  describe("participant view", () => {
    it("renders the detail with timeline and composer when user is a participant", () => {
      mockUseThread.mockReturnValue({
        data: makeThread(),
        isPending: false,
        error: null,
      });

      render(<EngagementDetail threadId="thread-abc" />);
      expect(screen.getByTestId("engagement-detail")).toBeInTheDocument();
      expect(screen.getByTestId("mock-timeline")).toBeInTheDocument();
      expect(screen.getByTestId("mock-composer")).toBeInTheDocument();
    });

    it("does NOT show the observe banner for participants", () => {
      mockUseThread.mockReturnValue({
        data: makeThread(),
        isPending: false,
        error: null,
      });

      render(<EngagementDetail threadId="thread-abc" />);
      expect(
        screen.queryByTestId("engagement-observe-banner"),
      ).not.toBeInTheDocument();
    });

    it("does NOT show an observer badge for participants", () => {
      mockUseThread.mockReturnValue({
        data: makeThread(),
        isPending: false,
        error: null,
      });

      render(<EngagementDetail threadId="thread-abc" />);
      expect(screen.queryByText("Observer")).not.toBeInTheDocument();
    });
  });

  describe("observer view", () => {
    beforeEach(() => {
      // Current user is NOT in the thread's participant list.
      mockUseCurrentUser.mockReturnValue({
        data: { id: "99999999-9999-9999-9999-999999999999", address: "human://other" },
        isPending: false,
        error: null,
      });
    });

    it("shows the observe banner for non-participants", () => {
      mockUseThread.mockReturnValue({
        data: makeThread(),
        isPending: false,
        error: null,
      });

      render(<EngagementDetail threadId="thread-abc" />);
      expect(
        screen.getByTestId("engagement-observe-banner"),
      ).toBeInTheDocument();
    });

    it("shows an Observer badge for non-participants", () => {
      mockUseThread.mockReturnValue({
        data: makeThread(),
        isPending: false,
        error: null,
      });

      render(<EngagementDetail threadId="thread-abc" />);
      expect(screen.getByText("Observer")).toBeInTheDocument();
    });

    it("does NOT show the composer for non-participants", () => {
      mockUseThread.mockReturnValue({
        data: makeThread(),
        isPending: false,
        error: null,
      });

      render(<EngagementDetail threadId="thread-abc" />);
      expect(screen.queryByTestId("mock-composer")).not.toBeInTheDocument();
    });

    it("shows the timeline for non-participants (read-only observe)", () => {
      mockUseThread.mockReturnValue({
        data: makeThread(),
        isPending: false,
        error: null,
      });

      render(<EngagementDetail threadId="thread-abc" />);
      expect(screen.getByTestId("mock-timeline")).toBeInTheDocument();
    });

    // #1630
    it("passes layout='timeline' to the timeline so observer view is left-justified", () => {
      mockUseThread.mockReturnValue({
        data: makeThread(),
        isPending: false,
        error: null,
      });

      render(<EngagementDetail threadId="thread-abc" />);
      const timeline = screen.getByTestId("mock-timeline");
      expect(timeline).toHaveAttribute("data-layout", "timeline");
    });
  });

  // #2888: the regression that motivated this fix. The operator sends
  // into a unit-scoped engagement wearing their unit Hat; `/auth/me`
  // resolves a *different* Hat (the auth-username Hat). The pre-fix code
  // compared each participant id to the lone `/me.id`, so the operator —
  // the literal sender — was misclassified as a read-only observer.
  describe("multi-Hat participant (#2888)", () => {
    beforeEach(() => {
      // Authenticated as the auth-username Hat, which is NOT the Hat
      // stamped on this thread.
      mockUseCurrentUser.mockReturnValue({
        data: AUTH_USERNAME_HAT,
        isPending: false,
        error: null,
      });
      mockUseThread.mockReturnValue({
        data: makeThread(),
        isPending: false,
        error: null,
      });
    });

    it("shows the composer when a bound Hat — not the /me Hat — is the participant", () => {
      // Bound set: the auth-username Hat plus the unit-scoped sending Hat.
      // The sending Hat (not `/me.id`) is the thread participant.
      mockUseCallerHumans.mockReturnValue({
        data: [
          { humanId: AUTH_USERNAME_HAT.id, isPrimary: true },
          { humanId: UNIT_SENDING_HAT_ID, isPrimary: false },
        ],
        isPending: false,
        error: null,
      });

      render(<EngagementDetail threadId="thread-abc" />);

      expect(screen.getByTestId("mock-composer")).toBeInTheDocument();
      expect(
        screen.queryByTestId("engagement-observe-banner"),
      ).not.toBeInTheDocument();
      expect(screen.queryByText("Observer")).not.toBeInTheDocument();
    });

    it("passes layout='dialog' to the timeline for a multi-Hat participant", () => {
      mockUseCallerHumans.mockReturnValue({
        data: [{ humanId: UNIT_SENDING_HAT_ID, isPrimary: true }],
        isPending: false,
        error: null,
      });

      render(<EngagementDetail threadId="thread-abc" />);
      expect(screen.getByTestId("mock-timeline")).toHaveAttribute(
        "data-layout",
        "dialog",
      );
    });

    it("omits the operator's own sending Hat from the header participant list", () => {
      mockUseCallerHumans.mockReturnValue({
        data: [{ humanId: UNIT_SENDING_HAT_ID, isPrimary: true }],
        isPending: false,
        error: null,
      });

      render(<EngagementDetail threadId="thread-abc" />);
      const header = screen.getByTestId("engagement-detail-header-names");
      // The non-human counterpart ("ada") still shows; the operator's own
      // Hat ("savas") is filtered out as self even though its id ≠ `/me.id`.
      expect(header.textContent).toContain("ada");
      expect(header.textContent).not.toContain("savas");
    });

    it("still shows the observe banner when no bound Hat is a participant", () => {
      // A bound Hat that is NOT on the thread — proves the bound-set gate
      // is not vacuously true.
      mockUseCallerHumans.mockReturnValue({
        data: [
          { humanId: "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", isPrimary: true },
        ],
        isPending: false,
        error: null,
      });

      render(<EngagementDetail threadId="thread-abc" />);
      expect(
        screen.getByTestId("engagement-observe-banner"),
      ).toBeInTheDocument();
      expect(screen.queryByTestId("mock-composer")).not.toBeInTheDocument();
    });
  });

  describe("participant view layout (#1630)", () => {
    it("passes layout='dialog' to the timeline so participants see chat bubbles", () => {
      mockUseThread.mockReturnValue({
        data: makeThread(),
        isPending: false,
        error: null,
      });

      render(<EngagementDetail threadId="thread-abc" />);
      const timeline = screen.getByTestId("mock-timeline");
      expect(timeline).toHaveAttribute("data-layout", "dialog");
    });
  });

  // #1630
  describe("header name resolution", () => {
    it("renders 'Unknown participant' rather than a raw GUID when displayName is missing", () => {
      const id = "d4ce4258-ab40-4c10-be06-407cc5ec9139";
      mockUseThread.mockReturnValue({
        data: makeThread({
          participants: [
            { id: "11111111-1111-1111-1111-111111111111", address: "human://savas", displayName: "savas" },
            // identity form, no displayName — the wire shape that
            // produced the GUID titles in the issue screenshot.
            { id, address: `agent:id:${id}`, displayName: "" },
          ],
        }),
        isPending: false,
        error: null,
      });

      render(<EngagementDetail threadId="thread-abc" />);
      const header = screen.getByTestId("engagement-detail-header-names");
      expect(header.textContent).not.toMatch(id);
      expect(header.textContent).not.toMatch(/agent:id:/);
    });
  });

  describe("question CTA (E2.6)", () => {
    it("shows the question CTA when there is a pending inbox item for the thread", () => {
      mockUseThread.mockReturnValue({
        data: makeThread(),
        isPending: false,
        error: null,
      });
      mockUseInbox.mockReturnValue({
        data: [makeInboxItem()],
        isPending: false,
        error: null,
      });

      render(<EngagementDetail threadId="thread-abc" />);
      expect(
        screen.getByTestId("engagement-question-cta"),
      ).toBeInTheDocument();
      expect(
        screen.getByTestId("engagement-answer-button"),
      ).toBeInTheDocument();
    });

    it("does NOT show the question CTA when inbox is empty", () => {
      mockUseThread.mockReturnValue({
        data: makeThread(),
        isPending: false,
        error: null,
      });
      mockUseInbox.mockReturnValue({
        data: [],
        isPending: false,
        error: null,
      });

      render(<EngagementDetail threadId="thread-abc" />);
      expect(
        screen.queryByTestId("engagement-question-cta"),
      ).not.toBeInTheDocument();
    });

    it("does NOT show the question CTA when the inbox item is for a different thread", () => {
      mockUseThread.mockReturnValue({
        data: makeThread(),
        isPending: false,
        error: null,
      });
      mockUseInbox.mockReturnValue({
        data: [makeInboxItem({ threadId: "thread-other" })],
        isPending: false,
        error: null,
      });

      render(<EngagementDetail threadId="thread-abc" />);
      expect(
        screen.queryByTestId("engagement-question-cta"),
      ).not.toBeInTheDocument();
    });

    it("does NOT show the question CTA for non-participants (observers)", () => {
      mockUseCurrentUser.mockReturnValue({
        data: { id: "99999999-9999-9999-9999-999999999999", address: "human://other" },
        isPending: false,
        error: null,
      });
      mockUseThread.mockReturnValue({
        data: makeThread(),
        isPending: false,
        error: null,
      });
      mockUseInbox.mockReturnValue({
        data: [makeInboxItem()],
        isPending: false,
        error: null,
      });

      render(<EngagementDetail threadId="thread-abc" />);
      expect(
        screen.queryByTestId("engagement-question-cta"),
      ).not.toBeInTheDocument();
    });

    it("clicking 'Answer this question' switches composer to answer mode", () => {
      mockUseThread.mockReturnValue({
        data: makeThread(),
        isPending: false,
        error: null,
      });
      mockUseInbox.mockReturnValue({
        data: [makeInboxItem()],
        isPending: false,
        error: null,
      });

      render(<EngagementDetail threadId="thread-abc" />);
      // Composer starts in information mode.
      expect(screen.getByTestId("mock-composer")).toHaveAttribute(
        "data-kind",
        "information",
      );

      // Click the CTA answer button.
      fireEvent.click(screen.getByTestId("engagement-answer-button"));

      // Composer should switch to answer mode.
      expect(screen.getByTestId("mock-composer")).toHaveAttribute(
        "data-kind",
        "answer",
      );
    });

    it("hides the question CTA when composer is already in answer mode", () => {
      mockUseThread.mockReturnValue({
        data: makeThread(),
        isPending: false,
        error: null,
      });
      mockUseInbox.mockReturnValue({
        data: [makeInboxItem()],
        isPending: false,
        error: null,
      });

      render(<EngagementDetail threadId="thread-abc" />);
      // Trigger answer mode.
      fireEvent.click(screen.getByTestId("engagement-answer-button"));

      // CTA should be hidden (would create a feedback loop).
      expect(
        screen.queryByTestId("engagement-question-cta"),
      ).not.toBeInTheDocument();
    });

    it("resets composer to information mode after a successful send", () => {
      mockUseThread.mockReturnValue({
        data: makeThread(),
        isPending: false,
        error: null,
      });
      mockUseInbox.mockReturnValue({
        data: [makeInboxItem()],
        isPending: false,
        error: null,
      });

      render(<EngagementDetail threadId="thread-abc" />);
      // Trigger answer mode.
      fireEvent.click(screen.getByTestId("engagement-answer-button"));
      expect(screen.getByTestId("mock-composer")).toHaveAttribute(
        "data-kind",
        "answer",
      );

      // Simulate successful send.
      fireEvent.click(screen.getByTestId("mock-send-success"));
      expect(screen.getByTestId("mock-composer")).toHaveAttribute(
        "data-kind",
        "information",
      );
    });
  });
});
