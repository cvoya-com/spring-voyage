import { render, screen } from "@testing-library/react";
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

const useRouterMock = vi.fn(() => ({ replace: vi.fn() }));
const searchParamsStateMock = { value: "" };
vi.mock("next/navigation", () => ({
  useRouter: () => useRouterMock(),
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

import UnitMessagesTab from "./unit-messages";

describe("UnitMessagesTab", () => {
  const node: UnitNode = {
    kind: "Unit",
    id: "engineering",
    name: "Engineering",
    status: "running",
  };

  it("renders the empty state when no conversations", () => {
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
