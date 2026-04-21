import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { AgentNode } from "../aggregate";

vi.mock("next/link", () => ({
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

const routerReplaceMock = vi.fn();
const searchParamsStateMock = { value: "" };
vi.mock("next/navigation", () => ({
  useRouter: () => ({ replace: routerReplaceMock }),
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

vi.mock("@/components/conversation/new-conversation-dialog", () => ({
  NewConversationDialog: (props: {
    open: boolean;
    onClose: () => void;
    targetScheme: "unit" | "agent";
    targetPath: string;
    onCreated: (id: string) => void;
  }) => {
    if (!props.open) return null;
    return (
      <div
        data-testid="new-conversation-dialog-stub"
        data-target-scheme={props.targetScheme}
        data-target-path={props.targetPath}
      >
        <button
          type="button"
          data-testid="stub-emit-created"
          onClick={() => props.onCreated("conv-new")}
        />
      </div>
    );
  },
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
    expect(screen.getByTestId("new-conversation-trigger")).toBeInTheDocument();
  });

  it("opens the composer targeted at this agent", () => {
    searchParamsStateMock.value = "";
    useConversationsMock.mockReturnValue({
      data: [],
      isLoading: false,
      error: null,
    });
    render(<AgentMessagesTab node={node} path={[node]} />);
    fireEvent.click(screen.getByTestId("new-conversation-trigger"));
    const dialog = screen.getByTestId("new-conversation-dialog-stub");
    expect(dialog.dataset.targetScheme).toBe("agent");
    expect(dialog.dataset.targetPath).toBe("ada");
  });

  it("routes to the new thread when the composer reports success", () => {
    searchParamsStateMock.value = "";
    routerReplaceMock.mockReset();
    useConversationsMock.mockReturnValue({
      data: [],
      isLoading: false,
      error: null,
    });
    render(<AgentMessagesTab node={node} path={[node]} />);
    fireEvent.click(screen.getByTestId("new-conversation-trigger"));
    fireEvent.click(screen.getByTestId("stub-emit-created"));
    expect(routerReplaceMock).toHaveBeenCalledWith(
      expect.stringMatching(/conversation=conv-new/),
      expect.any(Object),
    );
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
