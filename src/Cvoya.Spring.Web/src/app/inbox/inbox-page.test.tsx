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
  },
  {
    threadId: "conv-2",
    from: "unit://design",
    human: "human://savas",
    pendingSince: new Date(Date.now() - 1000 * 60 * 30).toISOString(),
    summary: "Ready to ship the portal redesign?",
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
});
