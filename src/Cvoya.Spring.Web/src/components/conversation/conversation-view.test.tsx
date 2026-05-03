// Tests for the shared <ConversationView> primitive (#1574 / #1575).
//
// The component drives engagement detail, the unit/agent Messages tab,
// and the inbox right pane. The surfaces around it are tested
// separately — these tests pin the contract owned by the primitive
// itself: filter dropdown, event rendering, custom header/empty slots,
// the row-actions affordance switch, and the `detail` prop bypass.

import { fireEvent, render, screen } from "@testing-library/react";
import type { ReactNode } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { ThreadDetail } from "@/lib/api/types";

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

const useThreadMock = vi.fn();
vi.mock("@/lib/api/queries", () => ({
  useThread: (id: string, opts?: unknown) => useThreadMock(id, opts),
}));

vi.mock("@/lib/stream/use-thread-stream", () => ({
  useThreadStream: () => ({ connected: false, events: [] }),
}));

import { ConversationView } from "./conversation-view";

function makeDetail(events: ThreadDetail["events"]): ThreadDetail {
  return {
    summary: {
      id: "t-1",
      status: "active",
      participants: [
        { address: "human://savas", displayName: "savas" },
        { address: "agent://ada", displayName: "ada" },
      ],
      lastActivity: "2026-04-30T10:00:00Z",
      createdAt: "2026-04-30T09:00:00Z",
      eventCount: events.length,
      origin: { address: "human://savas", displayName: "savas" },
      summary: "Test thread",
    },
    events,
  };
}

const messageEvent = {
  id: "e-msg",
  eventType: "MessageReceived",
  source: { address: "agent://ada", displayName: "ada" },
  timestamp: "2026-04-30T10:00:00Z",
  severity: "Info",
  summary: "envelope summary",
  body: "actual reply text",
};

const lifecycleEvent = {
  id: "e-state",
  eventType: "StateChanged",
  source: { address: "agent://ada", displayName: "ada" },
  timestamp: "2026-04-30T10:01:00Z",
  severity: "Info",
  summary: "state changed",
};

beforeEach(() => {
  // Default the mock to a benign "no data" return so tests that don't
  // care about the query (e.g. the `detail` bypass case) don't trip
  // over an undefined query result. Tests that care override this.
  useThreadMock.mockReset();
  useThreadMock.mockReturnValue({
    data: undefined,
    isPending: false,
    isFetching: false,
    error: null,
  });
});

describe("ConversationView — fetch states", () => {
  it("renders the loading skeleton while the thread query is pending", () => {
    useThreadMock.mockReturnValue({
      data: undefined,
      isPending: true,
      isFetching: true,
      error: null,
    });
    render(<ConversationView threadId="t-1" testId="cv" />);
    expect(screen.getByTestId("cv-loading")).toBeInTheDocument();
  });

  it("renders the error alert when the thread query fails", () => {
    useThreadMock.mockReturnValue({
      data: undefined,
      isPending: false,
      isFetching: false,
      error: new Error("boom"),
    });
    render(<ConversationView threadId="t-1" testId="cv" />);
    const alert = screen.getByTestId("cv-error");
    expect(alert).toHaveAttribute("role", "alert");
    expect(alert).toHaveTextContent("boom");
  });

  it("renders the not-found state when no data is returned", () => {
    useThreadMock.mockReturnValue({
      data: null,
      isPending: false,
      isFetching: false,
      error: null,
    });
    render(<ConversationView threadId="t-1" testId="cv" />);
    expect(screen.getByTestId("cv-not-found")).toBeInTheDocument();
  });
});

describe("ConversationView — filter behaviour", () => {
  beforeEach(() => {
    useThreadMock.mockReturnValue({
      data: makeDetail([messageEvent, lifecycleEvent]),
      isPending: false,
      isFetching: false,
      error: null,
    });
  });

  it("defaults to the Messages filter and hides lifecycle events", () => {
    render(<ConversationView threadId="t-1" />);
    expect(screen.getByTestId("conversation-event-e-msg")).toBeInTheDocument();
    // Lifecycle events render as cards (#1630). Whether the bubble or
    // card variant — both should be absent under the Messages filter.
    expect(screen.queryByTestId("conversation-event-e-state")).toBeNull();
    expect(screen.queryByTestId("conversation-event-card-e-state")).toBeNull();
    expect(screen.getByTestId("timeline-filter-label")).toHaveTextContent(
      "Messages",
    );
  });

  it("shows all events under the Full timeline filter", () => {
    render(<ConversationView threadId="t-1" />);
    fireEvent.click(screen.getByTestId("timeline-filter-trigger"));
    fireEvent.click(screen.getByTestId("timeline-filter-option-full"));

    expect(screen.getByTestId("conversation-event-e-msg")).toBeInTheDocument();
    // Lifecycle events surface as cards in the Full filter (#1630).
    expect(
      screen.getByTestId("conversation-event-card-e-state"),
    ).toBeInTheDocument();
  });

  it("honours `defaultFilter` so callers can land on Full timeline", () => {
    render(<ConversationView threadId="t-1" defaultFilter="full" />);
    expect(screen.getByTestId("timeline-filter-label")).toHaveTextContent(
      "Full timeline",
    );
    expect(
      screen.getByTestId("conversation-event-card-e-state"),
    ).toBeInTheDocument();
  });

  it("renders the message body in place of the envelope summary", () => {
    render(<ConversationView threadId="t-1" />);
    const row = screen.getByTestId("conversation-event-e-msg");
    expect(row).toHaveTextContent("actual reply text");
    expect(row).not.toHaveTextContent("envelope summary");
  });
});

describe("ConversationView — row actions affordance", () => {
  beforeEach(() => {
    useThreadMock.mockReturnValue({
      data: makeDetail([messageEvent]),
      isPending: false,
      isFetching: false,
      error: null,
    });
  });

  it("renders the activity-link footer by default", () => {
    render(<ConversationView threadId="t-1" />);
    expect(
      screen.getByRole("link", { name: /open in activity log/i }),
    ).toBeInTheDocument();
    expect(
      screen.queryByTestId("conversation-event-meta-toggle"),
    ).toBeNull();
  });

  it("renders the metadata-toggle affordance when rowActions=metadata", () => {
    render(<ConversationView threadId="t-1" rowActions="metadata" />);
    expect(
      screen.queryByRole("link", { name: /open in activity log/i }),
    ).toBeNull();
    const toggle = screen.getByTestId("conversation-event-meta-toggle");
    fireEvent.click(toggle);
    expect(
      screen.getByTestId("conversation-event-meta-e-msg"),
    ).toBeInTheDocument();
  });

  it("forwards the `rowTestIdPrefix` to each row", () => {
    render(
      <ConversationView
        threadId="t-1"
        rowActions="metadata"
        rowTestIdPrefix="inbox-event"
      />,
    );
    expect(screen.getByTestId("inbox-event-e-msg")).toBeInTheDocument();
    fireEvent.click(screen.getByTestId("inbox-event-meta-toggle"));
    expect(screen.getByTestId("inbox-event-meta-e-msg")).toBeInTheDocument();
  });
});

describe("ConversationView — slots", () => {
  beforeEach(() => {
    useThreadMock.mockReturnValue({
      data: makeDetail([]),
      isPending: false,
      isFetching: false,
      error: null,
    });
  });

  it("uses `renderHeader` to replace the default header (filter dropdown is forwarded)", () => {
    render(
      <ConversationView
        threadId="t-1"
        renderHeader={(api) => (
          <div data-testid="custom-header">
            <span>custom-{api.totalEvents}</span>
            {api.filterDropdown}
          </div>
        )}
      />,
    );
    expect(screen.getByTestId("custom-header")).toHaveTextContent("custom-0");
    // The shared filter dropdown is still mounted via the slot.
    expect(screen.getByTestId("timeline-filter-trigger")).toBeInTheDocument();
  });

  it("uses `renderEmpty` to override the empty-state copy", () => {
    render(
      <ConversationView
        threadId="t-1"
        renderEmpty={() => (
          <p data-testid="custom-empty">Nothing here yet.</p>
        )}
      />,
    );
    expect(screen.getByTestId("custom-empty")).toHaveTextContent(
      "Nothing here yet.",
    );
  });
});

describe("ConversationView — `detail` prop bypass", () => {
  it("renders the supplied detail without issuing the thread query", () => {
    const detail = makeDetail([messageEvent]);
    render(<ConversationView threadId="t-1" detail={detail} />);
    expect(useThreadMock).toHaveBeenCalledTimes(1);
    expect(useThreadMock).toHaveBeenCalledWith(
      "t-1",
      expect.objectContaining({ enabled: false }),
    );
    expect(screen.getByTestId("conversation-event-e-msg")).toBeInTheDocument();
  });
});

// #1630
describe("ConversationView — observer-view timeline layout", () => {
  beforeEach(() => {
    useThreadMock.mockReturnValue({
      data: makeDetail([messageEvent, lifecycleEvent]),
      isPending: false,
      isFetching: false,
      error: null,
    });
  });

  it("renders non-message events as cards (not bubbles) in timeline mode", () => {
    render(
      <ConversationView
        threadId="t-1"
        layout="timeline"
        defaultFilter="full"
      />,
    );
    expect(
      screen.getByTestId("conversation-event-card-e-state"),
    ).toBeInTheDocument();
  });

  it("force-left-justifies message bubbles in timeline mode (no dialog axis)", () => {
    const { container } = render(
      <ConversationView
        threadId="t-1"
        layout="timeline"
        defaultFilter="full"
      />,
    );
    // The wrapper around the message bubble carries data-layout=timeline-row
    // when timeline mode forces left alignment.
    const wrappers = container.querySelectorAll(
      "[data-layout='timeline-row']",
    );
    expect(wrappers.length).toBeGreaterThan(0);
    wrappers.forEach((w) => expect(w.className).toMatch(/justify-start/));
  });

  it("annotates the events container with data-layout='timeline'", () => {
    render(
      <ConversationView
        threadId="t-1"
        layout="timeline"
        defaultFilter="full"
        testId="cv-tl"
      />,
    );
    expect(
      screen.getByTestId("cv-tl-events").getAttribute("data-layout"),
    ).toBe("timeline");
  });
});

describe("ConversationView — generic-event card fallback in dialog mode (#1630)", () => {
  it("renders body-less MessageReceived events as cards (not envelope-summary bubbles)", () => {
    const id = "d4ce4258-ab40-4c10-be06-407cc5ec9139";
    const bodylessMessage = {
      id: "e-bare",
      eventType: "MessageReceived",
      source: { address: "agent://ada", displayName: "ada" },
      timestamp: "2026-04-30T10:02:00Z",
      severity: "Info",
      summary: `Received Domain message ${id} from human:id:${id}`,
    };
    useThreadMock.mockReturnValue({
      data: makeDetail([bodylessMessage]),
      isPending: false,
      isFetching: false,
      error: null,
    });
    render(<ConversationView threadId="t-1" defaultFilter="full" />);
    const card = screen.getByTestId("conversation-event-card-e-bare");
    expect(card).toBeInTheDocument();
    // Compact-state MUST NOT leak the GUID; it lives behind the expand
    // affordance only.
    expect(card).not.toHaveTextContent(id);
  });
});

describe("ConversationView — shouldHideEvent", () => {
  it("filters out events that match the predicate before the filter runs", () => {
    useThreadMock.mockReturnValue({
      data: makeDetail([messageEvent, lifecycleEvent]),
      isPending: false,
      isFetching: false,
      error: null,
    });
    render(
      <ConversationView
        threadId="t-1"
        defaultFilter="full"
        shouldHideEvent={(e) => e.id === "e-state"}
      />,
    );
    expect(screen.getByTestId("conversation-event-e-msg")).toBeInTheDocument();
    expect(screen.queryByTestId("conversation-event-e-state")).toBeNull();
  });
});
