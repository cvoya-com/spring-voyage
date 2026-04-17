import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, expect, it, vi, beforeEach } from "vitest";
import type { ReactNode } from "react";

import { ConversationDetailClient } from "./conversation-detail-client";

const mockGetConversation = vi.fn();
const mockSendConversationMessage = vi.fn();

vi.mock("@/lib/api/client", () => ({
  api: {
    getConversation: (...args: unknown[]) => mockGetConversation(...args),
    sendConversationMessage: (...args: unknown[]) =>
      mockSendConversationMessage(...args),
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

const baseConversation = {
  summary: {
    id: "conv-42",
    participants: ["human://savas", "agent://ada"],
    status: "active",
    lastActivity: new Date().toISOString(),
    createdAt: new Date().toISOString(),
    eventCount: 3,
    origin: "human://savas",
    summary: "PR review thread",
  },
  events: [
    {
      id: "00000000-0000-0000-0000-000000000001",
      timestamp: new Date().toISOString(),
      source: "human://savas",
      eventType: "MessageSent",
      severity: "Info",
      summary: "Hey can you take a look?",
    },
    {
      id: "00000000-0000-0000-0000-000000000002",
      timestamp: new Date().toISOString(),
      source: "agent://ada",
      eventType: "MessageReceived",
      severity: "Info",
      summary: "Sure, looking now.",
    },
    {
      id: "00000000-0000-0000-0000-000000000003",
      timestamp: new Date().toISOString(),
      source: "agent://ada",
      eventType: "DecisionMade",
      severity: "Info",
      summary: "Calling code-search tool",
    },
  ],
};

function Wrapper({ children }: { children: ReactNode }) {
  const client = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0, staleTime: 0 },
    },
  });
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

describe("ConversationDetailClient", () => {
  beforeEach(() => {
    mockGetConversation.mockReset();
    mockSendConversationMessage.mockReset();
    mockGetConversation.mockResolvedValue(baseConversation);
  });

  it("renders the thread with role-attributed events", async () => {
    render(
      <Wrapper>
        <ConversationDetailClient id="conv-42" />
      </Wrapper>,
    );
    await waitFor(() => {
      expect(screen.getByText("Hey can you take a look?")).toBeInTheDocument();
      expect(screen.getByText("Sure, looking now.")).toBeInTheDocument();
    });

    // The human message bubble carries the human role.
    const humanRow = screen.getByTestId(
      "conversation-event-00000000-0000-0000-0000-000000000001",
    );
    expect(humanRow).toHaveAttribute("data-role", "human");

    // DecisionMade renders as a tool-call (collapsed by default).
    const toolRow = screen.getByTestId(
      "conversation-event-00000000-0000-0000-0000-000000000003",
    );
    expect(toolRow).toHaveAttribute("data-role", "tool");
    expect(
      toolRow.querySelector('[aria-expanded="false"]'),
    ).not.toBeNull();
  });

  it("links the origin to the activity surface", async () => {
    render(
      <Wrapper>
        <ConversationDetailClient id="conv-42" />
      </Wrapper>,
    );
    await waitFor(() => {
      const originLink = screen.getByLabelText("Open origin in activity log");
      expect(originLink).toHaveAttribute(
        "href",
        "/activity?source=human%3A%2F%2Fsavas",
      );
    });
  });

  it("sends a message via the conversation API on composer submit", async () => {
    mockSendConversationMessage.mockResolvedValue({
      messageId: "msg-1",
      conversationId: "conv-42",
      responsePayload: null,
    });
    render(
      <Wrapper>
        <ConversationDetailClient id="conv-42" />
      </Wrapper>,
    );
    await waitFor(() => {
      expect(screen.getByLabelText("Recipient address")).toBeInTheDocument();
    });

    fireEvent.change(screen.getByLabelText("Message text"), {
      target: { value: "Looks good, ship it." },
    });
    fireEvent.click(screen.getByRole("button", { name: /send/i }));

    await waitFor(() => {
      expect(mockSendConversationMessage).toHaveBeenCalledWith("conv-42", {
        to: { scheme: "agent", path: "ada" },
        text: "Looks good, ship it.",
      });
    });
  });

  it("shows a not-found state when the API returns null", async () => {
    mockGetConversation.mockResolvedValue(null);
    render(
      <Wrapper>
        <ConversationDetailClient id="missing" />
      </Wrapper>,
    );
    await waitFor(() => {
      expect(
        screen.getByText(/was not found/i),
      ).toBeInTheDocument();
    });
  });
});
