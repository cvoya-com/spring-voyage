import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { AgentNode } from "../aggregate";

vi.mock("next/link", () => ({
  // Strip next/link-only props so React doesn't warn about them being
  // forwarded to a plain <a>.
  default: ({
    href,
    children,
    replace: _replace,
    scroll: _scroll,
    prefetch: _prefetch,
    ...rest
  }: {
    href: string;
    children: React.ReactNode;
    replace?: boolean;
    scroll?: boolean;
    prefetch?: boolean;
  } & Record<string, unknown>) => (
    <a href={href} {...rest}>
      {children}
    </a>
  ),
}));

const searchParamsStateMock = { value: "" };
vi.mock("next/navigation", () => ({
  useRouter: () => ({ replace: vi.fn() }),
  useSearchParams: () => new URLSearchParams(searchParamsStateMock.value),
}));

vi.mock("@/components/conversation/conversation-detail-pane", () => ({
  ConversationDetailPane: ({
    conversationId,
    selfAddress,
  }: {
    conversationId: string;
    selfAddress?: string;
  }) => (
    <div
      data-testid="detail-pane-stub"
      data-conversation-id={conversationId}
      data-self-address={selfAddress ?? ""}
    />
  ),
}));

const useConversationsMock = vi.fn();
vi.mock("@/lib/api/queries", () => ({
  useConversations: (filters: unknown) => useConversationsMock(filters),
}));

import AgentMessagesTab from "./agent-messages";

describe("AgentMessagesTab", () => {
  const node: AgentNode = {
    kind: "Agent",
    id: "ada",
    name: "Ada",
    status: "running",
  };

  it("filters conversations by agent id", () => {
    searchParamsStateMock.value = "";
    useConversationsMock.mockReturnValueOnce({
      data: [],
      isLoading: false,
      error: null,
    });
    render(<AgentMessagesTab node={node} path={[node]} />);
    expect(useConversationsMock).toHaveBeenCalledWith({ agent: "ada" });
    expect(screen.getByTestId("tab-agent-messages-empty")).toBeInTheDocument();
  });

  it("mounts the detail pane when the URL carries ?conversation=<id>", () => {
    searchParamsStateMock.value = "conversation=abc";
    useConversationsMock.mockReturnValueOnce({
      data: [
        {
          id: "abc",
          summary: "Ada drafts a PR",
          lastActivity: "2026-04-20T00:00:00Z",
          status: "open",
        },
      ],
      isLoading: false,
      error: null,
    });
    render(<AgentMessagesTab node={node} path={[node]} />);
    const pane = screen.getByTestId("detail-pane-stub");
    expect(pane.dataset.conversationId).toBe("abc");
    expect(pane.dataset.selfAddress).toBe("agent://ada");
  });

  it("does not link to the retired /conversations/<id> route", () => {
    searchParamsStateMock.value = "";
    useConversationsMock.mockReturnValueOnce({
      data: [
        {
          id: "abc",
          summary: "Ada drafts a PR",
          lastActivity: "2026-04-20T00:00:00Z",
          status: "open",
        },
      ],
      isLoading: false,
      error: null,
    });
    const { container } = render(
      <AgentMessagesTab node={node} path={[node]} />,
    );
    const anchors = container.querySelectorAll("a");
    for (const a of anchors) {
      expect(a.getAttribute("href") ?? "").not.toMatch(/^\/conversations\//);
    }
  });
});
