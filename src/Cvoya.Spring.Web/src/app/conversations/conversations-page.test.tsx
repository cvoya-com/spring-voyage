import { render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, expect, it, vi, beforeEach } from "vitest";
import type { ReactNode } from "react";

import ConversationsPage from "./page";

const mockListConversations = vi.fn();
const mockListInbox = vi.fn();

vi.mock("@/lib/api/client", () => ({
  api: {
    listConversations: (...args: unknown[]) => mockListConversations(...args),
    listInbox: (...args: unknown[]) => mockListInbox(...args),
  },
}));

vi.mock("@/lib/stream/use-activity-stream", () => ({
  useActivityStream: () => ({ events: [], connected: false }),
}));

vi.mock("next/navigation", () => ({
  useRouter: () => ({ replace: vi.fn(), push: vi.fn() }),
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

function Wrapper({ children }: { children: ReactNode }) {
  const client = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0, staleTime: 0 },
    },
  });
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

const conversations = [
  {
    id: "conv-1",
    participants: ["human://savas", "agent://ada"],
    status: "active",
    lastActivity: new Date().toISOString(),
    createdAt: new Date().toISOString(),
    eventCount: 4,
    origin: "human://savas",
    summary: "Help me debug the auth flow",
  },
  {
    id: "conv-2",
    participants: ["unit://eng"],
    status: "completed",
    lastActivity: new Date().toISOString(),
    createdAt: new Date().toISOString(),
    eventCount: 12,
    origin: "unit://eng",
    summary: "Standup",
  },
];

describe("ConversationsPage", () => {
  beforeEach(() => {
    mockListConversations.mockReset();
    mockListInbox.mockReset();
    mockListConversations.mockResolvedValue(conversations);
    mockListInbox.mockResolvedValue([]);
  });

  it("renders the conversation list from the API", async () => {
    render(
      <Wrapper>
        <ConversationsPage />
      </Wrapper>,
    );
    await waitFor(() => {
      expect(
        screen.getByTestId("conversation-card-conv-1"),
      ).toBeInTheDocument();
      expect(
        screen.getByTestId("conversation-card-conv-2"),
      ).toBeInTheDocument();
    });
  });

  it("shows the empty inbox message when there are no asks", async () => {
    render(
      <Wrapper>
        <ConversationsPage />
      </Wrapper>,
    );
    await waitFor(() => {
      expect(
        screen.getByText("No conversations are waiting on you."),
      ).toBeInTheDocument();
    });
  });

  it("renders inbox items as links to the corresponding conversation", async () => {
    mockListInbox.mockResolvedValue([
      {
        conversationId: "conv-7",
        from: "agent://ada",
        human: "human://savas",
        pendingSince: new Date().toISOString(),
        summary: "Need your call on this design choice",
      },
    ]);
    render(
      <Wrapper>
        <ConversationsPage />
      </Wrapper>,
    );
    await waitFor(() => {
      const link = screen.getByText("Need your call on this design choice");
      expect(link.closest("a")).toHaveAttribute(
        "href",
        "/conversations/conv-7",
      );
    });
  });

  it("shows an empty state when the conversation list is empty", async () => {
    mockListConversations.mockResolvedValue([]);
    render(
      <Wrapper>
        <ConversationsPage />
      </Wrapper>,
    );
    await waitFor(() => {
      expect(
        screen.getByText("No conversations match these filters."),
      ).toBeInTheDocument();
    });
  });
});
