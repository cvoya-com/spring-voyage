import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { UnitNode } from "../aggregate";

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

// Stub the dialog so the tab tests can assert open-state + invoke the
// `onCreated` callback without pulling the real composer (which has its
// own test file and would drag in TanStack Query setup).
const dialogRenders = vi.fn();
vi.mock("@/components/conversation/new-conversation-dialog", () => ({
  NewConversationDialog: (props: {
    open: boolean;
    onClose: () => void;
    targetScheme: "unit" | "agent";
    targetPath: string;
    onCreated: (id: string) => void;
  }) => {
    dialogRenders(props);
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

import UnitMessagesTab from "./unit-messages";

describe("UnitMessagesTab", () => {
  const node: UnitNode = {
    kind: "Unit",
    id: "engineering",
    name: "Engineering",
    status: "running",
  };

  it("renders the empty state plus a '+ New conversation' trigger when there are no threads", () => {
    searchParamsStateMock.value = "";
    useConversationsMock.mockReturnValueOnce({
      data: [],
      isLoading: false,
      error: null,
    });
    render(<UnitMessagesTab node={node} path={[node]} />);
    expect(screen.getByTestId("tab-unit-messages-empty")).toHaveTextContent(
      "No conversations",
    );
    expect(screen.getByTestId("new-conversation-trigger")).toBeInTheDocument();
  });

  it("filters conversations by unit id and renders the list", () => {
    searchParamsStateMock.value = "";
    useConversationsMock.mockReturnValueOnce({
      data: [
        {
          id: "abc",
          summary: "Ada asks about build",
          lastActivity: "2026-04-20T00:00:00Z",
          status: "open",
        },
      ],
      isLoading: false,
      error: null,
    });
    render(<UnitMessagesTab node={node} path={[node]} />);
    expect(useConversationsMock).toHaveBeenCalledWith({ unit: "engineering" });
    expect(screen.getByText("Ada asks about build")).toBeInTheDocument();
    // No selection yet — detail pane stub should not render.
    expect(screen.queryByTestId("detail-pane-stub")).toBeNull();
  });

  it("renders the '+ New conversation' trigger even with existing threads", () => {
    searchParamsStateMock.value = "";
    useConversationsMock.mockReturnValueOnce({
      data: [
        {
          id: "abc",
          summary: "Ada asks about build",
          lastActivity: "2026-04-20T00:00:00Z",
          status: "open",
        },
      ],
      isLoading: false,
      error: null,
    });
    render(<UnitMessagesTab node={node} path={[node]} />);
    expect(screen.getByTestId("new-conversation-trigger")).toBeInTheDocument();
  });

  it("opens the composer dialog on '+ New conversation' click and targets the unit", () => {
    searchParamsStateMock.value = "";
    useConversationsMock.mockReturnValue({
      data: [],
      isLoading: false,
      error: null,
    });
    render(<UnitMessagesTab node={node} path={[node]} />);
    expect(screen.queryByTestId("new-conversation-dialog-stub")).toBeNull();
    fireEvent.click(screen.getByTestId("new-conversation-trigger"));
    const dialog = screen.getByTestId("new-conversation-dialog-stub");
    expect(dialog.dataset.targetScheme).toBe("unit");
    expect(dialog.dataset.targetPath).toBe("engineering");
  });

  it("routes to the new thread when the composer reports success", () => {
    searchParamsStateMock.value = "";
    routerReplaceMock.mockReset();
    useConversationsMock.mockReturnValue({
      data: [],
      isLoading: false,
      error: null,
    });
    render(<UnitMessagesTab node={node} path={[node]} />);
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
          summary: "Ada asks about build",
          lastActivity: "2026-04-20T00:00:00Z",
          status: "open",
        },
      ],
      isLoading: false,
      error: null,
    });
    render(<UnitMessagesTab node={node} path={[node]} />);
    const pane = screen.getByTestId("detail-pane-stub");
    expect(pane.dataset.conversationId).toBe("abc");
    expect(pane.dataset.selfAddress).toBe("unit://engineering");
  });

  it("does not link to the retired /conversations/<id> route", () => {
    searchParamsStateMock.value = "";
    useConversationsMock.mockReturnValueOnce({
      data: [
        {
          id: "abc",
          summary: "Ada asks about build",
          lastActivity: "2026-04-20T00:00:00Z",
          status: "open",
        },
      ],
      isLoading: false,
      error: null,
    });
    const { container } = render(
      <UnitMessagesTab node={node} path={[node]} />,
    );
    const anchors = container.querySelectorAll("a");
    for (const a of anchors) {
      expect(a.getAttribute("href") ?? "").not.toMatch(/^\/conversations\//);
    }
  });
});
