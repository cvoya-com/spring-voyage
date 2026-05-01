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
const mockUseInbox = vi.fn();

vi.mock("@/lib/api/queries", () => ({
  useThread: (...args: unknown[]) => mockUseThread(...args),
  useCurrentUser: (...args: unknown[]) => mockUseCurrentUser(...args),
  useInbox: (...args: unknown[]) => mockUseInbox(...args),
}));

// Mock child components to isolate the detail logic.
vi.mock("./engagement-timeline", () => ({
  EngagementTimeline: ({ threadId }: { threadId: string }) => (
    <div data-testid="mock-timeline" data-thread-id={threadId} />
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
        { address: "human://savas", displayName: "savas" },
        { address: "agent://ada", displayName: "ada" },
      ],
      status: "active",
      lastActivity: new Date().toISOString(),
      createdAt: new Date().toISOString(),
      eventCount: 3,
      origin: { address: "human://savas", displayName: "savas" },
      summary: "Test engagement",
      ...overrides,
    },
    events: [],
  };
}

function makeInboxItem(overrides: Partial<InboxItem> = {}): InboxItem {
  return {
    threadId: "thread-abc",
    from: { address: "agent://ada", displayName: "ada" },
    human: { address: "human://savas", displayName: "savas" },
    pendingSince: new Date().toISOString(),
    summary: "Which branch?",
    unreadCount: 0,
    ...overrides,
  };
}

const CURRENT_USER = { address: "human://savas" };

// ── tests ──────────────────────────────────────────────────────────────────

describe("EngagementDetail", () => {
  beforeEach(() => {
    mockUseInbox.mockReturnValue({ data: [], isPending: false, error: null });
    mockUseCurrentUser.mockReturnValue({
      data: CURRENT_USER,
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
  });

  describe("error state", () => {
    it("shows an error alert when the thread query fails", () => {
      mockUseThread.mockReturnValue({
        data: undefined,
        isPending: false,
        error: new Error("Not found"),
      });

      render(<EngagementDetail threadId="thread-abc" />);
      const alert = screen.getByTestId("engagement-detail-error");
      expect(alert).toBeInTheDocument();
      expect(alert).toHaveAttribute("role", "alert");
      expect(screen.getByText(/Not found/)).toBeInTheDocument();
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
        data: { address: "human://other" },
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
        data: { address: "human://other" },
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
