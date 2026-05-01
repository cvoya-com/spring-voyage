// Inbox page tests — redesigned two-pane list-detail layout (#1474).
//
// Tests cover:
//   - thread rows rendered in the left pane
//   - auto-selection / ?thread= routing via router.replace
//   - deep-link URL carried through the "Open" link
//   - empty state
//   - error state
//   - header with count badge and mirror-CLI note

import { render, screen, waitFor } from "@testing-library/react";
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

const mockRouterReplace = vi.fn();

vi.mock("@/lib/api/queries", () => ({
  useInbox: () => ({
    data: _inboxData,
    error: _inboxError,
    isPending: _inboxPending,
    isFetching: false,
    refetch: vi.fn(),
  }),
  useThread: () => ({ data: null, isPending: false, error: null, isFetching: false }),
  useMarkInboxRead: () => ({
    mutate: _markReadMutate,
    isPending: false,
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
  useSearchParams: () => new URLSearchParams(""),
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
  parseThreadSource: (src: string) => ({ raw: src, scheme: "agent", path: src }),
  roleFromEvent: () => "agent",
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

const rows: InboxItem[] = [
  {
    threadId: "conv-1",
    from: "agent://engineering-team/ada",
    human: "human://savas",
    pendingSince: new Date(Date.now() - 1000 * 60 * 10).toISOString(),
    summary: "Need your call on the migration plan",
    unreadCount: 3,
  },
  {
    threadId: "conv-2",
    from: "unit://design",
    human: "human://savas",
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

import InboxPage from "./page";

describe("InboxPage (redesigned #1474)", () => {
  beforeEach(() => {
    _inboxData = null;
    _inboxError = null;
    _inboxPending = false;
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

  it("auto-selects the first thread via router.replace when no ?thread= param is set", async () => {
    setupInbox(rows);
    render(
      <Wrapper>
        <InboxPage />
      </Wrapper>,
    );
    await waitFor(() => {
      expect(mockRouterReplace).toHaveBeenCalledWith(
        expect.stringContaining("conv-1"),
      );
    });
  });

  it("renders the thread label from the from:// address", async () => {
    setupInbox(rows);
    render(
      <Wrapper>
        <InboxPage />
      </Wrapper>,
    );
    await waitFor(() => {
      // "engineering-team/ada" is the path stripped from "agent://engineering-team/ada"
      expect(
        screen.getByTestId("inbox-row-label-conv-1"),
      ).toHaveTextContent("engineering-team/ada");
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

  it("renders the header with a mirror-CLI note", async () => {
    setupInbox([]);
    render(
      <Wrapper>
        <InboxPage />
      </Wrapper>,
    );
    await waitFor(() => {
      expect(
        screen.getByRole("heading", { name: /Inbox/ }),
      ).toBeInTheDocument();
      expect(screen.getByText("spring inbox list")).toBeInTheDocument();
    });
  });

  it("shows the count badge when inbox items exist", async () => {
    setupInbox(rows);
    render(
      <Wrapper>
        <InboxPage />
      </Wrapper>,
    );
    await waitFor(() => {
      const badge = screen.getByTestId("inbox-count-badge");
      expect(badge).toBeInTheDocument();
      expect(badge).toHaveTextContent(String(rows.length));
    });
  });

  // --- Unread badge tests (#1477) ---

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
    const mixed: InboxItem[] = [
      {
        threadId: "read-thread",
        from: "agent://alice",
        human: "human://savas",
        pendingSince: new Date(Date.now() - 1000 * 60 * 5).toISOString(),
        summary: "Read thread",
        unreadCount: 0,
      },
      {
        threadId: "unread-thread",
        from: "agent://bob",
        human: "human://savas",
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
