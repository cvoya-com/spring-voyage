import { render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, expect, it, vi, beforeEach } from "vitest";
import type { ReactNode } from "react";

import InboxPage from "./page";

const mockListInbox = vi.fn();

vi.mock("@/lib/api/client", () => ({
  api: {
    listInbox: (...args: unknown[]) => mockListInbox(...args),
  },
}));

vi.mock("@/lib/stream/use-activity-stream", () => ({
  useActivityStream: () => ({ events: [], connected: false }),
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

function Wrapper({ children }: { children: ReactNode }) {
  const client = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0, staleTime: 0 },
    },
  });
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

const rows = [
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

describe("InboxPage", () => {
  beforeEach(() => {
    mockListInbox.mockReset();
  });

  it("renders one card per inbox row", async () => {
    mockListInbox.mockResolvedValue(rows);
    render(
      <Wrapper>
        <InboxPage />
      </Wrapper>,
    );
    await waitFor(() => {
      expect(screen.getByTestId("inbox-card-conv-1")).toBeInTheDocument();
      expect(screen.getByTestId("inbox-card-conv-2")).toBeInTheDocument();
    });
  });

  it("cards deep-link to the conversation thread", async () => {
    mockListInbox.mockResolvedValue(rows);
    render(
      <Wrapper>
        <InboxPage />
      </Wrapper>,
    );
    await waitFor(() => {
      const open = screen.getByTestId("inbox-open-conv-1");
      expect(open).toHaveAttribute("href", "/inbox?thread=conv-1");
    });
  });

  it("shows the empty state when no items are waiting", async () => {
    mockListInbox.mockResolvedValue([]);
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
    mockListInbox.mockRejectedValue(new Error("boom"));
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
    mockListInbox.mockResolvedValue([]);
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
    mockListInbox.mockResolvedValue(rows);
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
