// Inbox page tests — redesigned two-pane list-detail layout (#1474, #1482).
//
// Tests cover:
//   - thread rows rendered in the left pane
//   - auto-selection / ?thread= routing via router.replace
//   - deep-link URL carried through the "Open" link
//   - empty state
//   - error state
//   - header copy (#1482): no CLI mirror sentence, updated subtitle
//   - thread row label uses display name derived from address path (#1482)
//   - timeline (i) info button opens the address popover (#1482)
//   - timeline/messages dropdown switches what events render (#1482)
//   - user's own MessageReceived event renders the body text, not a placeholder (#1482)

import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, expect, it, vi, beforeEach } from "vitest";
import type { ReactNode } from "react";
import type { InboxItem } from "@/lib/api/types";

// Mutable state captured by vi.mock factory closures.
// Each test calls setupInbox() to control what useInbox() returns.
let _inboxData: InboxItem[] | null = null;
let _inboxError: Error | null = null;
let _inboxPending = false;
const _markReadMutate = vi.fn();

// Thread detail returned by useThread — controls right-pane rendering.
interface MockParticipantRef {
  address: string;
  displayName: string;
}
interface MockThreadDetail {
  summary: {
    id: string;
    status: string;
    participants: MockParticipantRef[];
  };
  events: Array<{
    id: string;
    eventType: string;
    source: MockParticipantRef;
    timestamp: string;
    severity: string;
    summary: string;
    body?: string | null;
  }>;
}
let _threadData: MockThreadDetail | null = null;

const mockRouterReplace = vi.fn();

vi.mock("@/lib/api/queries", () => ({
  useInbox: () => ({
    data: _inboxData,
    error: _inboxError,
    isPending: _inboxPending,
    isFetching: false,
    refetch: vi.fn(),
  }),
  useThread: () => ({
    data: _threadData,
    isPending: false,
    error: null,
    isFetching: false,
  }),
  useMarkInboxRead: () => ({
    mutate: _markReadMutate,
    isPending: false,
  }),
  useCurrentUser: () => ({
    data: { address: "human://savas" },
    isPending: false,
    error: null,
  }),
}));

vi.mock("@/lib/stream/use-activity-stream", () => ({
  useActivityStream: () => ({ events: [], connected: false }),
}));

vi.mock("@/lib/stream/use-thread-stream", () => ({
  useThreadStream: () => ({ connected: false }),
}));

vi.mock("next/navigation", () => ({
  useRouter: () => ({ replace: mockRouterReplace, push: vi.fn() }),
  useSearchParams: () => new URLSearchParams("thread=conv-1"),
}));

vi.mock("next/link", () => ({
  default: ({
    href,
    children,
    ...rest
  }: {
    href: string;
    children: React.ReactNode;
  } & Record<string, unknown>) => (
    <a href={href} {...rest}>
      {children}
    </a>
  ),
}));

vi.mock("@/components/thread/role", () => ({
  // Mirrors the real parseThreadSource — handles both navigation (scheme://path)
  // and identity (scheme:id:<uuid>) forms (#1490).
  parseThreadSource: (src: string) => {
    const idIdx = src.indexOf(":id:");
    if (idIdx > 0) {
      const scheme = src.slice(0, idIdx).toLowerCase();
      const path = src.slice(idIdx + 4);
      if (path && !path.includes("/") && !path.includes(":")) {
        return { raw: src, scheme, path, kind: "identity" };
      }
    }
    const navIdx = src.indexOf("://");
    if (navIdx > 0) {
      return { raw: src, scheme: src.slice(0, navIdx).toLowerCase(), path: src.slice(navIdx + 3), kind: "navigation" };
    }
    return { raw: src, scheme: "system", path: src, kind: "navigation" };
  },
  roleFromEvent: (_src: string, eventType: string) => {
    if (eventType === "DecisionMade") return "tool";
    // Match both navigation (scheme://) and identity (scheme:id:) forms.
    const scheme = _src.includes(":id:") ? _src.slice(0, _src.indexOf(":id:")).toLowerCase()
      : _src.includes("://") ? _src.slice(0, _src.indexOf("://")).toLowerCase()
      : "system";
    if (scheme === "human") return "human";
    if (scheme === "agent") return "agent";
    if (scheme === "unit") return "unit";
    return "system";
  },
  ROLE_STYLES: {
    agent: { align: "start", label: "Agent", bubble: "bg-muted" },
    human: { align: "end", label: "Human", bubble: "bg-primary/10" },
    system: { align: "start", label: "System", bubble: "bg-muted" },
    tool: { align: "start", label: "Tool", bubble: "bg-muted/60" },
    error: { align: "start", label: "Error", bubble: "bg-destructive/10" },
  },
}));

function Wrapper({ children }: { children: ReactNode }) {
  const client = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0, staleTime: 0 },
    },
  });
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

// Agent and unit participants now carry the identity form (#1490).
// Human participants keep the navigation form until #1491 lands.
const ADA_ID = "a1b2c3d4-0000-0000-0000-000000000001";
const DESIGN_ID = "a1b2c3d4-0000-0000-0000-000000000002";

const rows: InboxItem[] = [
  {
    threadId: "conv-1",
    from: { address: `agent:id:${ADA_ID}`, displayName: "engineering-team/ada" },
    human: { address: "human://savas", displayName: "savas" },
    pendingSince: new Date(Date.now() - 1000 * 60 * 10).toISOString(),
    summary: "Need your call on the migration plan",
    unreadCount: 3,
  },
  {
    threadId: "conv-2",
    from: { address: `unit:id:${DESIGN_ID}`, displayName: "design" },
    human: { address: "human://savas", displayName: "savas" },
    pendingSince: new Date(Date.now() - 1000 * 60 * 30).toISOString(),
    summary: "Ready to ship the portal redesign?",
    unreadCount: 0,
  },
];

function setupInbox(
  data: InboxItem[] | null,
  error: Error | null = null,
  pending = false,
) {
  _inboxData = data;
  _inboxError = error;
  _inboxPending = pending;
}

function setupThread(data: MockThreadDetail | null) {
  _threadData = data;
}

import InboxPage from "./page";

describe("InboxPage — layout and navigation (#1474)", () => {
  beforeEach(() => {
    _inboxData = null;
    _inboxError = null;
    _inboxPending = false;
    _threadData = null;
    mockRouterReplace.mockReset();
    _markReadMutate.mockReset();
  });

  it("renders one thread row per inbox item in the left pane", async () => {
    setupInbox(rows);
    render(
      <Wrapper>
        <InboxPage />
      </Wrapper>,
    );
    await waitFor(() => {
      expect(screen.getByTestId("inbox-thread-row-conv-1")).toBeInTheDocument();
      expect(screen.getByTestId("inbox-thread-row-conv-2")).toBeInTheDocument();
    });
  });

  it("shows the empty state when no items are waiting", async () => {
    setupInbox([]);
    render(
      <Wrapper>
        <InboxPage />
      </Wrapper>,
    );
    await waitFor(() => {
      expect(screen.getByTestId("inbox-empty")).toBeInTheDocument();
      expect(screen.getByText("Nothing waiting on you.")).toBeInTheDocument();
    });
  });

  it("shows the error state when the request fails", async () => {
    setupInbox(null, new Error("boom"));
    render(
      <Wrapper>
        <InboxPage />
      </Wrapper>,
    );
    await waitFor(() => {
      expect(screen.getByTestId("inbox-error")).toBeInTheDocument();
      expect(screen.getByText(/Failed to load inbox/)).toBeInTheDocument();
    });
  });
});

describe("InboxPage — header copy (#1482)", () => {
  beforeEach(() => {
    _inboxData = null;
    _inboxError = null;
    _inboxPending = false;
    _threadData = null;
    mockRouterReplace.mockReset();
    _markReadMutate.mockReset();
  });

  it("renders the Inbox heading without a count badge", async () => {
    setupInbox(rows);
    render(
      <Wrapper>
        <InboxPage />
      </Wrapper>,
    );
    await waitFor(() => {
      expect(
        screen.getByRole("heading", { name: /Inbox/ }),
      ).toBeInTheDocument();
      // Count badge must be absent (#1482 — moved to per-thread in #1477)
      expect(screen.queryByTestId("inbox-count-badge")).toBeNull();
    });
  });

  it("renders the updated subtitle without the CLI mirror sentence", async () => {
    setupInbox([]);
    render(
      <Wrapper>
        <InboxPage />
      </Wrapper>,
    );
    await waitFor(() => {
      const subtitle = screen.getByTestId("inbox-subtitle");
      expect(subtitle).toHaveTextContent("Engagements with you as a participant");
      // Old CLI mirror text must be gone
      expect(screen.queryByText(/spring inbox list/)).toBeNull();
    });
  });
});

describe("InboxPage — thread row label uses display names (#1482)", () => {
  beforeEach(() => {
    _inboxData = null;
    _inboxError = null;
    _inboxPending = false;
    _threadData = null;
    mockRouterReplace.mockReset();
    _markReadMutate.mockReset();
  });

  it("shows the display name as the row label", async () => {
    setupInbox(rows);
    render(
      <Wrapper>
        <InboxPage />
      </Wrapper>,
    );
    await waitFor(() => {
      // displayName from the ParticipantRef is used for the row label (#1490:
      // "from" now carries the identity form, but the API resolves displayName)
      expect(
        screen.getByTestId("inbox-row-label-conv-1"),
      ).toHaveTextContent("engineering-team/ada");
      // displayName from the unit identity-form ParticipantRef
      expect(
        screen.getByTestId("inbox-row-label-conv-2"),
      ).toHaveTextContent("design");
    });
  });
});

describe("InboxPage — timeline participant popover (#1482)", () => {
  beforeEach(() => {
    _inboxData = rows;
    _inboxError = null;
    _inboxPending = false;
    _threadData = null;
    mockRouterReplace.mockReset();
    _markReadMutate.mockReset();
  });

  it("renders participant names in the timeline header when thread has participants", async () => {
    // Agents now carry the identity form (#1490).
    const agentAddr = `agent:id:${ADA_ID}`;
    setupThread({
      summary: {
        id: "conv-1",
        status: "active",
        participants: [
          { address: "human://savas", displayName: "savas" },
          { address: agentAddr, displayName: "ada" },
        ],
      },
      events: [],
    });
    render(
      <Wrapper>
        <InboxPage />
      </Wrapper>,
    );
    await waitFor(() => {
      // "ada" displayed (human:// excluded); test-id is keyed off the address string.
      expect(
        screen.getByTestId(`participant-name-${agentAddr}`),
      ).toHaveTextContent("ada");
    });
  });

  it("opens the address popover when the (i) info button is clicked", async () => {
    const agentAddr = `agent:id:${ADA_ID}`;
    setupThread({
      summary: {
        id: "conv-1",
        status: "active",
        participants: [
          { address: "human://savas", displayName: "savas" },
          { address: agentAddr, displayName: "ada" },
        ],
      },
      events: [],
    });
    render(
      <Wrapper>
        <InboxPage />
      </Wrapper>,
    );
    // Wait for participant info button to appear
    const infoBtn = await screen.findByTestId(`participant-info-btn-${agentAddr}`);
    fireEvent.click(infoBtn);
    // Popover should appear with the full identity-form address
    const popover = screen.getByTestId(`participant-popover-${agentAddr}`);
    expect(popover).toBeInTheDocument();
    expect(popover).toHaveTextContent(agentAddr);
  });
});

describe("InboxPage — timeline/messages dropdown (#1482)", () => {
  beforeEach(() => {
    _inboxData = rows;
    _inboxError = null;
    _inboxPending = false;
    _threadData = null;
    mockRouterReplace.mockReset();
    _markReadMutate.mockReset();
  });

  it("renders the filter dropdown defaulting to Messages", async () => {
    const agentAddr = `agent:id:${ADA_ID}`;
    setupThread({
      summary: {
        id: "conv-1",
        status: "active",
        participants: [
          { address: "human://savas", displayName: "savas" },
          { address: agentAddr, displayName: "ada" },
        ],
      },
      events: [],
    });
    render(
      <Wrapper>
        <InboxPage />
      </Wrapper>,
    );
    await waitFor(() => {
      const label = screen.getByTestId("timeline-filter-label");
      expect(label).toHaveTextContent("Messages");
    });
  });

  it("filters to only MessageReceived events under Messages mode", async () => {
    const agentAddr = `agent:id:${ADA_ID}`;
    setupThread({
      summary: {
        id: "conv-1",
        status: "active",
        participants: [
          { address: "human://savas", displayName: "savas" },
          { address: agentAddr, displayName: "ada" },
        ],
      },
      events: [
        {
          id: "e-msg",
          eventType: "MessageReceived",
          source: { address: agentAddr, displayName: "ada" },
          timestamp: "2026-04-30T10:00:00Z",
          severity: "Info",
          summary: "hello",
          body: "hello world",
        },
        {
          id: "e-state",
          eventType: "StateChanged",
          source: { address: agentAddr, displayName: "ada" },
          timestamp: "2026-04-30T10:01:00Z",
          severity: "Info",
          summary: "state changed",
        },
      ],
    });
    render(
      <Wrapper>
        <InboxPage />
      </Wrapper>,
    );
    await waitFor(() => {
      // MessageReceived event should be visible
      expect(screen.getByTestId("inbox-event-e-msg")).toBeInTheDocument();
      // StateChanged event should be hidden under "Messages" filter
      expect(screen.queryByTestId("inbox-event-e-state")).toBeNull();
    });
  });

  it("shows all events when switched to Full timeline", async () => {
    const agentAddr = `agent:id:${ADA_ID}`;
    setupThread({
      summary: {
        id: "conv-1",
        status: "active",
        participants: [
          { address: "human://savas", displayName: "savas" },
          { address: agentAddr, displayName: "ada" },
        ],
      },
      events: [
        {
          id: "e-msg",
          eventType: "MessageReceived",
          source: { address: agentAddr, displayName: "ada" },
          timestamp: "2026-04-30T10:00:00Z",
          severity: "Info",
          summary: "hello",
          body: "hello world",
        },
        {
          id: "e-state",
          eventType: "StateChanged",
          source: { address: agentAddr, displayName: "ada" },
          timestamp: "2026-04-30T10:01:00Z",
          severity: "Info",
          summary: "state changed",
        },
      ],
    });
    render(
      <Wrapper>
        <InboxPage />
      </Wrapper>,
    );
    // Open the dropdown and switch to "Full timeline"
    const trigger = await screen.findByTestId("timeline-filter-trigger");
    fireEvent.click(trigger);
    const fullOption = screen.getByTestId("timeline-filter-option-full");
    fireEvent.click(fullOption);

    await waitFor(() => {
      expect(screen.getByTestId("inbox-event-e-msg")).toBeInTheDocument();
      expect(screen.getByTestId("inbox-event-e-state")).toBeInTheDocument();
    });
  });
});

describe("InboxPage — user's own message renders text, not placeholder (#1482)", () => {
  beforeEach(() => {
    _inboxData = rows;
    _inboxError = null;
    _inboxPending = false;
    _threadData = null;
    mockRouterReplace.mockReset();
    _markReadMutate.mockReset();
  });

  it("renders the body text of a user MessageReceived event", async () => {
    const agentAddr = `agent:id:${ADA_ID}`;
    setupThread({
      summary: {
        id: "conv-1",
        status: "active",
        participants: [
          { address: "human://savas", displayName: "savas" },
          { address: agentAddr, displayName: "ada" },
        ],
      },
      events: [
        {
          id: "e-human",
          eventType: "MessageReceived",
          source: { address: "human://savas", displayName: "savas" },
          timestamp: "2026-04-30T10:00:00Z",
          severity: "Info",
          summary: "Received Domain message from human://savas",
          body: "Can you help me with this?",
        },
      ],
    });
    render(
      <Wrapper>
        <InboxPage />
      </Wrapper>,
    );
    await waitFor(() => {
      const eventEl = screen.getByTestId("inbox-event-e-human");
      // Body text should appear, not the "received domain message" placeholder
      expect(eventEl).toHaveTextContent("Can you help me with this?");
      expect(eventEl).not.toHaveTextContent("Received Domain message from human://savas");
    });
  });
});

describe("InboxPage — unread badge and mark-read (#1477)", () => {
  beforeEach(() => {
    _inboxData = null;
    _inboxError = null;
    _inboxPending = false;
    _threadData = null;
    mockRouterReplace.mockReset();
    _markReadMutate.mockReset();
  });

  it("renders the (N) unread badge when unreadCount > 0", async () => {
    setupInbox(rows);
    render(
      <Wrapper>
        <InboxPage />
      </Wrapper>,
    );
    await waitFor(() => {
      // conv-1 has unreadCount=3 → badge should be visible.
      const badge = screen.getByTestId("inbox-unread-badge-conv-1");
      expect(badge).toBeInTheDocument();
      expect(badge).toHaveTextContent("(3)");
    });
  });

  it("does not render the unread badge when unreadCount is 0", async () => {
    setupInbox(rows);
    render(
      <Wrapper>
        <InboxPage />
      </Wrapper>,
    );
    await waitFor(() => {
      // conv-2 has unreadCount=0 → no badge.
      expect(
        screen.queryByTestId("inbox-unread-badge-conv-2"),
      ).not.toBeInTheDocument();
    });
  });

  it("fires mark-read mutation when a thread is selected", async () => {
    setupInbox(rows);
    const { getByTestId } = render(
      <Wrapper>
        <InboxPage />
      </Wrapper>,
    );
    await waitFor(() => {
      expect(getByTestId("inbox-thread-row-conv-1")).toBeInTheDocument();
    });

    getByTestId("inbox-thread-row-conv-1").click();

    await waitFor(() => {
      expect(_markReadMutate).toHaveBeenCalledWith("conv-1");
    });
  });

  it("sorts unread threads before read threads", async () => {
    const aliceId = "b1b2b3b4-0000-0000-0000-000000000010";
    const bobId = "b1b2b3b4-0000-0000-0000-000000000011";
    const mixed: InboxItem[] = [
      {
        threadId: "read-thread",
        from: { address: `agent:id:${aliceId}`, displayName: "alice" },
        human: { address: "human://savas", displayName: "savas" },
        pendingSince: new Date(Date.now() - 1000 * 60 * 5).toISOString(),
        summary: "Read thread",
        unreadCount: 0,
      },
      {
        threadId: "unread-thread",
        from: { address: `agent:id:${bobId}`, displayName: "bob" },
        human: { address: "human://savas", displayName: "savas" },
        // older, but should still sort first because it has unread events
        pendingSince: new Date(Date.now() - 1000 * 60 * 60).toISOString(),
        summary: "Unread thread",
        unreadCount: 5,
      },
    ];
    setupInbox(mixed);
    render(
      <Wrapper>
        <InboxPage />
      </Wrapper>,
    );
    await waitFor(() => {
      const rows = screen.getAllByTestId(/^inbox-thread-row-/);
      // The unread thread must appear first despite being older.
      expect(rows[0]).toHaveAttribute("data-testid", "inbox-thread-row-unread-thread");
      expect(rows[1]).toHaveAttribute("data-testid", "inbox-thread-row-read-thread");
    });
  });
});
