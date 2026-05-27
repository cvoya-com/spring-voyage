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
//   - user's own MessageArrived event renders the body text, not a placeholder (#1482)

import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, expect, it, vi, beforeEach } from "vitest";
import type { ReactNode } from "react";
import type { CallerHumanResponse, InboxItem } from "@/lib/api/types";

// Mutable state captured by vi.mock factory closures.
// Each test calls setupInbox() to control what useInbox() returns.
let _inboxData: InboxItem[] | null = null;
let _inboxError: Error | null = null;
let _inboxPending = false;
const _markReadMutate = vi.fn();

// Thread detail returned by useThread — controls right-pane rendering.
interface MockParticipantRef {
  id: string;
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
let _callerHumans: CallerHumanResponse[] = [];

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
    data: {
      id: "11111111-1111-1111-1111-111111111111",
      address: "human://savas",
    },
    isPending: false,
    error: null,
  }),
  // ADR-0062 § 5: the inbox page (and <MessageComposer>) reads the
  // caller's bound Hats. The default is an empty list to keep the
  // selector hidden; toolbar-filter tests override via setCallerHumans.
  useCallerHumans: () => ({
    data: _callerHumans,
    isLoading: false,
    isError: false,
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

vi.mock("@/components/thread/role", async (importOriginal) => {
  // Use the real role module — the inbox surface tests cover end-to-end
  // rendering, and the hand-rolled stubs that lived here drifted from
  // the real helpers when #1630 added addressOf / participantDisplayName
  // and the UUID-shaped-path fallback rules.
  return await importOriginal<typeof import("@/components/thread/role")>();
});

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
    from: { id: ADA_ID, address: `agent:id:${ADA_ID}`, displayName: "engineering-team/ada" },
    human: { id: "11111111-1111-1111-1111-111111111111", address: "human://savas", displayName: "savas" },
    pendingSince: new Date(Date.now() - 1000 * 60 * 10).toISOString(),
    summary: "Need your call on the migration plan",
    unreadCount: 3,
  },
  {
    threadId: "conv-2",
    from: { id: DESIGN_ID, address: `unit:id:${DESIGN_ID}`, displayName: "design" },
    human: { id: "11111111-1111-1111-1111-111111111111", address: "human://savas", displayName: "savas" },
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

function setCallerHumans(data: CallerHumanResponse[]) {
  _callerHumans = data;
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
    _callerHumans = [];
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
    });
    // No "API error N" prose — the translator-routed render keeps the
    // friendly copy for the lead message (#2157, #2163).
    expect(screen.queryByText(/API error/)).not.toBeInTheDocument();
  });

  it("renders translated copy when the inbox load throws ProblemDetails (#2163)", async () => {
    const { ApiError } = await import("@/lib/api/client");
    setupInbox(
      null,
      new ApiError(404, "Not Found", {
        type: "https://cvoya.com/problems/unit-not-found",
        title: "Not Found",
        status: 404,
        detail: "UnitNotFound: scope is gone.",
        code: "UnitNotFound",
        traceId: "00-inbox",
      }),
    );
    render(
      <Wrapper>
        <InboxPage />
      </Wrapper>,
    );
    await waitFor(() => {
      expect(screen.getByTestId("inbox-error")).toBeInTheDocument();
    });
    expect(screen.getByText(/Unit not found\./)).toBeInTheDocument();
    expect(
      screen.getByText(/It may have been deleted\. Refresh the page/),
    ).toBeInTheDocument();
    expect(screen.queryByText(/API error 404/)).not.toBeInTheDocument();
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
    _callerHumans = [];
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
    _callerHumans = [];
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
    _callerHumans = [];
  });

  it("renders participant names in the timeline header when thread has participants", async () => {
    // Agents now carry the identity form (#1490).
    const agentAddr = `agent:id:${ADA_ID}`;
    setupThread({
      summary: {
        id: "conv-1",
        status: "active",
        participants: [
          { id: "11111111-1111-1111-1111-111111111111", address: "human://savas", displayName: "savas" },
          { id: ADA_ID, address: agentAddr, displayName: "ada" },
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
          { id: "11111111-1111-1111-1111-111111111111", address: "human://savas", displayName: "savas" },
          { id: ADA_ID, address: agentAddr, displayName: "ada" },
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
    _callerHumans = [];
  });

  it("renders the filter dropdown defaulting to Messages", async () => {
    const agentAddr = `agent:id:${ADA_ID}`;
    setupThread({
      summary: {
        id: "conv-1",
        status: "active",
        participants: [
          { id: "11111111-1111-1111-1111-111111111111", address: "human://savas", displayName: "savas" },
          { id: ADA_ID, address: agentAddr, displayName: "ada" },
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

  it("filters to only MessageArrived events under Messages mode", async () => {
    const agentAddr = `agent:id:${ADA_ID}`;
    setupThread({
      summary: {
        id: "conv-1",
        status: "active",
        participants: [
          { id: "11111111-1111-1111-1111-111111111111", address: "human://savas", displayName: "savas" },
          { id: ADA_ID, address: agentAddr, displayName: "ada" },
        ],
      },
      events: [
        {
          id: "e-msg",
          eventType: "MessageArrived",
          source: { id: ADA_ID, address: agentAddr, displayName: "ada" },
          timestamp: "2026-04-30T10:00:00Z",
          severity: "Info",
          summary: "hello",
          body: "hello world",
        },
        {
          id: "e-state",
          eventType: "StateChanged",
          source: { id: ADA_ID, address: agentAddr, displayName: "ada" },
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
      // MessageArrived event should be visible
      expect(screen.getByTestId("inbox-event-e-msg")).toBeInTheDocument();
      // StateChanged event renders as a card (#1630) and should be hidden
      // under the "Messages" filter regardless of bubble vs card variant.
      expect(screen.queryByTestId("inbox-event-e-state")).toBeNull();
      expect(screen.queryByTestId("inbox-event-card-e-state")).toBeNull();
    });
  });

  it("shows all events when switched to Full timeline", async () => {
    const agentAddr = `agent:id:${ADA_ID}`;
    setupThread({
      summary: {
        id: "conv-1",
        status: "active",
        participants: [
          { id: "11111111-1111-1111-1111-111111111111", address: "human://savas", displayName: "savas" },
          { id: ADA_ID, address: agentAddr, displayName: "ada" },
        ],
      },
      events: [
        {
          id: "e-msg",
          eventType: "MessageArrived",
          source: { id: ADA_ID, address: agentAddr, displayName: "ada" },
          timestamp: "2026-04-30T10:00:00Z",
          severity: "Info",
          summary: "hello",
          body: "hello world",
        },
        {
          id: "e-state",
          eventType: "StateChanged",
          source: { id: ADA_ID, address: agentAddr, displayName: "ada" },
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
      // StateChanged surfaces as a click-to-expand card (#1630).
      expect(
        screen.getByTestId("inbox-event-card-e-state"),
      ).toBeInTheDocument();
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
    _callerHumans = [];
  });

  it("renders the body text of a user MessageArrived event", async () => {
    const agentAddr = `agent:id:${ADA_ID}`;
    setupThread({
      summary: {
        id: "conv-1",
        status: "active",
        participants: [
          { id: "11111111-1111-1111-1111-111111111111", address: "human://savas", displayName: "savas" },
          { id: ADA_ID, address: agentAddr, displayName: "ada" },
        ],
      },
      events: [
        {
          id: "e-human",
          eventType: "MessageArrived",
          source: { id: "11111111-1111-1111-1111-111111111111", address: "human://savas", displayName: "savas" },
          timestamp: "2026-04-30T10:00:00Z",
          severity: "Info",
          summary: "human message placeholder",
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
      // Body text should appear, not the engine-side summary placeholder.
      expect(eventEl).toHaveTextContent("Can you help me with this?");
      expect(eventEl).not.toHaveTextContent("human message placeholder");
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
    _callerHumans = [];
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

  it("renders the inline reply composer next to the timeline (#1574)", async () => {
    const agentAddr = `agent:id:${ADA_ID}`;
    setupInbox(rows);
    setupThread({
      summary: {
        id: "conv-1",
        status: "active",
        participants: [
          { id: "11111111-1111-1111-1111-111111111111", address: "human://savas", displayName: "savas" },
          { id: ADA_ID, address: agentAddr, displayName: "ada" },
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
      expect(screen.getByTestId("inbox-composer")).toBeInTheDocument();
      expect(screen.getByTestId("inbox-composer-input")).toBeInTheDocument();
      expect(screen.getByTestId("inbox-composer-send")).toBeInTheDocument();
      // Send button carries the keyboard-shortcut tooltip (#1553).
      expect(screen.getByTestId("inbox-composer-send")).toHaveAttribute(
        "title",
        "⌘/Ctrl+Enter to send",
      );
    });
  });

  it("sorts unread threads before read threads", async () => {
    const aliceId = "b1b2b3b4-0000-0000-0000-000000000010";
    const bobId = "b1b2b3b4-0000-0000-0000-000000000011";
    const mixed: InboxItem[] = [
      {
        threadId: "read-thread",
        from: { id: aliceId, address: `agent:id:${aliceId}`, displayName: "alice" },
        human: { id: "11111111-1111-1111-1111-111111111111", address: "human://savas", displayName: "savas" },
        pendingSince: new Date(Date.now() - 1000 * 60 * 5).toISOString(),
        summary: "Read thread",
        unreadCount: 0,
      },
      {
        threadId: "unread-thread",
        from: { id: bobId, address: `agent:id:${bobId}`, displayName: "bob" },
        human: { id: "11111111-1111-1111-1111-111111111111", address: "human://savas", displayName: "savas" },
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

// ---------------------------------------------------------------------------
// ADR-0062 § 5 — per-Hat inbox chip
// ---------------------------------------------------------------------------

describe("InboxPage — per-Hat chip (ADR-0062 § 5, #2807)", () => {
  beforeEach(() => {
    _inboxData = null;
    _inboxError = null;
    _inboxPending = false;
    _threadData = null;
    mockRouterReplace.mockReset();
    _markReadMutate.mockReset();
    _callerHumans = [];
  });

  it("renders a Hat chip per row labelled with the receiving Human's display name", async () => {
    setupInbox(rows);
    render(
      <Wrapper>
        <InboxPage />
      </Wrapper>,
    );

    const chip1 = await screen.findByTestId("inbox-hat-chip-conv-1");
    expect(chip1).toHaveTextContent("As savas");
    expect(chip1).toHaveAttribute("title", "Received as savas");

    const chip2 = await screen.findByTestId("inbox-hat-chip-conv-2");
    expect(chip2).toHaveTextContent("As savas");
  });

  it("renders the server-supplied disambiguated label on each row when available (#2829)", async () => {
    const savasId = "11111111-1111-1111-1111-111111111111";
    setupInbox(rows);
    setCallerHumans([
      {
        humanId: savasId,
        displayName: "savas",
        disambiguatedLabel: "savas — designer",
        isPrimary: true,
        memberships: [],
      },
      {
        humanId: "22222222-2222-2222-2222-222222222222",
        displayName: "savas",
        disambiguatedLabel: "savas — reviewer",
        isPrimary: false,
        memberships: [],
      },
    ]);
    render(
      <Wrapper>
        <InboxPage />
      </Wrapper>,
    );

    const chip = await screen.findByTestId("inbox-hat-chip-conv-1");
    expect(chip).toHaveTextContent("As savas — designer");
  });
});

// ---------------------------------------------------------------------------
// ADR-0062 § 5 + #2826 Part 2 — inbox toolbar Hat filter
// ---------------------------------------------------------------------------

describe("InboxPage — toolbar Hat filter (#2826 Part 2)", () => {
  beforeEach(() => {
    _inboxData = null;
    _inboxError = null;
    _inboxPending = false;
    _threadData = null;
    mockRouterReplace.mockReset();
    _markReadMutate.mockReset();
    _callerHumans = [];
  });

  it("hides the filter bar when the caller has fewer than 2 bound Hats", async () => {
    setupInbox(rows);
    setCallerHumans([
      {
        humanId: "11111111-1111-1111-1111-111111111111",
        displayName: "savas",
        disambiguatedLabel: "savas",
        isPrimary: true,
        memberships: [],
      },
    ]);
    render(
      <Wrapper>
        <InboxPage />
      </Wrapper>,
    );

    await waitFor(() => {
      expect(screen.getByTestId("inbox-page")).toBeInTheDocument();
    });
    expect(screen.queryByTestId("inbox-hat-filter")).not.toBeInTheDocument();
  });

  it("renders the filter bar with one chip per bound Hat plus All Hats", async () => {
    const bobId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    const aliceId = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
    setupInbox(rows);
    setCallerHumans([
      {
        humanId: bobId,
        displayName: "Bob",
        disambiguatedLabel: "Bob — designer",
        isPrimary: true,
        memberships: [],
      },
      {
        humanId: aliceId,
        displayName: "Alice",
        disambiguatedLabel: "Alice — reviewer",
        isPrimary: false,
        memberships: [],
      },
    ]);
    render(
      <Wrapper>
        <InboxPage />
      </Wrapper>,
    );

    const bar = await screen.findByTestId("inbox-hat-filter");
    expect(bar).toBeInTheDocument();
    expect(
      screen.getByTestId("inbox-hat-filter-chip-all"),
    ).toHaveTextContent("All Hats");
    expect(
      screen.getByTestId(`inbox-hat-filter-chip-${bobId}`),
    ).toHaveTextContent("As Bob — designer");
    expect(
      screen.getByTestId(`inbox-hat-filter-chip-${aliceId}`),
    ).toHaveTextContent("As Alice — reviewer");
  });

  it("filters the inbox list to only the chosen Hat's threads, restores on All Hats", async () => {
    const bobId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    const aliceId = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
    const filterRows: InboxItem[] = [
      {
        threadId: "thread-bob",
        from: { id: ADA_ID, address: `agent:id:${ADA_ID}`, displayName: "ada" },
        human: { id: bobId, address: `human:${bobId.replace(/-/g, "")}`, displayName: "Bob" },
        pendingSince: new Date().toISOString(),
        summary: "Bob got a question",
        unreadCount: 0,
      },
      {
        threadId: "thread-alice",
        from: { id: ADA_ID, address: `agent:id:${ADA_ID}`, displayName: "ada" },
        human: { id: aliceId, address: `human:${aliceId.replace(/-/g, "")}`, displayName: "Alice" },
        pendingSince: new Date().toISOString(),
        summary: "Alice got pinged",
        unreadCount: 0,
      },
    ];
    setupInbox(filterRows);
    setCallerHumans([
      {
        humanId: bobId,
        displayName: "Bob",
        disambiguatedLabel: "Bob — designer",
        isPrimary: true,
        memberships: [],
      },
      {
        humanId: aliceId,
        displayName: "Alice",
        disambiguatedLabel: "Alice — reviewer",
        isPrimary: false,
        memberships: [],
      },
    ]);
    render(
      <Wrapper>
        <InboxPage />
      </Wrapper>,
    );

    // Both rows visible by default ("All Hats").
    await waitFor(() => {
      expect(
        screen.getByTestId("inbox-thread-row-thread-bob"),
      ).toBeInTheDocument();
      expect(
        screen.getByTestId("inbox-thread-row-thread-alice"),
      ).toBeInTheDocument();
    });

    // Pick Bob → Alice's row drops.
    fireEvent.click(screen.getByTestId(`inbox-hat-filter-chip-${bobId}`));
    await waitFor(() => {
      expect(
        screen.getByTestId("inbox-thread-row-thread-bob"),
      ).toBeInTheDocument();
      expect(
        screen.queryByTestId("inbox-thread-row-thread-alice"),
      ).not.toBeInTheDocument();
    });

    // Click All Hats → both rows return.
    fireEvent.click(screen.getByTestId("inbox-hat-filter-chip-all"));
    await waitFor(() => {
      expect(
        screen.getByTestId("inbox-thread-row-thread-bob"),
      ).toBeInTheDocument();
      expect(
        screen.getByTestId("inbox-thread-row-thread-alice"),
      ).toBeInTheDocument();
    });
  });
});
