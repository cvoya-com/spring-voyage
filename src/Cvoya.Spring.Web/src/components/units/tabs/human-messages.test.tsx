// Tests for the Human × Messages tab body (#2268).
//
// Mirrors `agent-messages.test.tsx` / `unit-messages.test.tsx` — same
// canonical `<ConversationView>` primitive, same most-recently-active
// thread picker, same loading/error/empty pattern. Two deliberate
// differences from the Unit/Agent surface:
//
//   1. Threads are filtered by `participant=human:<id>`, not by
//      `unit=` / `agent=`.
//   2. No composer ships in v0.1 — the Human page is a view-only
//      observer surface. The tests below assert the composer is
//      absent.

import { render, screen } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import type { ReactNode } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { AgentNode, HumanNode } from "../aggregate";

vi.mock("next/navigation", () => ({
  useRouter: () => ({ replace: vi.fn(), push: vi.fn() }),
  usePathname: () => "/humans/11111111-1111-1111-1111-111111111111",
  useSearchParams: () => new URLSearchParams(""),
}));

vi.mock("next/link", () => ({
  default: ({
    href,
    children,
    ...rest
  }: {
    href: string;
    children: ReactNode;
  } & Record<string, unknown>) => (
    <a href={href} {...rest}>
      {children}
    </a>
  ),
}));

const useThreadsMock = vi.fn();
const useThreadMock = vi.fn();
const useHumanMock = vi.fn();
vi.mock("@/lib/api/queries", () => ({
  useThreads: (filters: unknown, opts?: unknown) =>
    useThreadsMock(filters, opts),
  useThread: (id: string, opts?: unknown) => useThreadMock(id, opts),
  useHuman: (id: string, opts?: unknown) => useHumanMock(id, opts),
}));

// The shared ConversationView opens a thread-scoped SSE stream for live
// updates. EventSource is not available in jsdom, so stub the hook so
// the timeline renders without firing the SSE side-effect.
vi.mock("@/lib/stream/use-thread-stream", () => ({
  useThreadStream: () => ({ connected: false, events: [] }),
}));

import HumanMessagesTab from "./human-messages";

function wrap(node: ReactNode) {
  const client = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0 },
      mutations: { retry: false },
    },
  });
  return <QueryClientProvider client={client}>{node}</QueryClientProvider>;
}

const HUMAN_ID = "11111111-1111-1111-1111-111111111111";

const humanNode: HumanNode = {
  kind: "Human",
  id: HUMAN_ID,
  name: "savas",
  status: "running",
};

beforeEach(() => {
  useThreadsMock.mockReset();
  useThreadMock.mockReset();
  useHumanMock.mockReset();
  useThreadMock.mockReturnValue({ data: null, isPending: false });
  useHumanMock.mockReturnValue({
    data: {
      id: HUMAN_ID,
      username: "savas",
      displayName: "Savas",
      email: "savas@example.com",
      platformRole: "Owner",
      createdAt: "2026-04-01T00:00:00Z",
    },
    isLoading: false,
    error: null,
  });
});

describe("HumanMessagesTab — filter contract (#2268)", () => {
  it("renders nothing for a non-Human node — guards against accidental cross-mount", () => {
    const node: AgentNode = {
      kind: "Agent",
      id: "ada",
      name: "Ada",
      status: "running",
    };
    const { container } = render(
      wrap(<HumanMessagesTab node={node} path={[node]} />),
    );
    expect(container.firstChild).toBeNull();
    expect(useThreadsMock).not.toHaveBeenCalled();
  });

  it("filters threads by `participant=human:<id>` — no unit/agent filter", () => {
    useThreadsMock.mockReturnValue({
      data: [],
      isLoading: false,
      error: null,
    });
    render(wrap(<HumanMessagesTab node={humanNode} path={[humanNode]} />));
    // (second arg is undefined because no options are passed)
    expect(useThreadsMock).toHaveBeenCalledWith(
      { participant: `human:${HUMAN_ID}` },
      undefined,
    );
  });
});

describe("HumanMessagesTab — loading + error states", () => {
  it("renders a skeleton row triple while threads are loading", () => {
    useThreadsMock.mockReturnValue({
      data: undefined,
      isLoading: true,
      error: null,
    });
    render(wrap(<HumanMessagesTab node={humanNode} path={[humanNode]} />));
    expect(
      screen.getByTestId("tab-human-messages-loading"),
    ).toBeInTheDocument();
  });

  it("surfaces a thread-list error through the shared `<ApiErrorMessage>`", () => {
    useThreadsMock.mockReturnValue({
      data: undefined,
      isLoading: false,
      error: new Error("API error 500: boom"),
    });
    render(wrap(<HumanMessagesTab node={humanNode} path={[humanNode]} />));
    expect(screen.getByTestId("tab-human-messages-error")).toBeInTheDocument();
  });
});

describe("HumanMessagesTab — empty state", () => {
  it("renders 'No messages yet for <name>' when the thread list is empty", () => {
    useThreadsMock.mockReturnValue({
      data: [],
      isLoading: false,
      error: null,
    });
    render(wrap(<HumanMessagesTab node={humanNode} path={[humanNode]} />));
    const empty = screen.getByTestId("tab-human-messages-empty");
    expect(empty).toHaveTextContent(/No messages yet/);
    // The display name from `useHuman(id)` ("Savas") wins over the
    // tree-side `name` ("savas") on the Human node.
    expect(empty).toHaveTextContent(/Savas/);
  });

  it("falls back to the tree-side `node.name` while `useHuman` is pending", () => {
    useThreadsMock.mockReturnValue({
      data: [],
      isLoading: false,
      error: null,
    });
    useHumanMock.mockReturnValue({
      data: undefined,
      isLoading: true,
      error: null,
    });
    render(wrap(<HumanMessagesTab node={humanNode} path={[humanNode]} />));
    expect(
      screen.getByTestId("tab-human-messages-empty"),
    ).toHaveTextContent(/savas/);
  });

  it("does NOT render a composer — Human page is view-only (#2268)", () => {
    useThreadsMock.mockReturnValue({
      data: [],
      isLoading: false,
      error: null,
    });
    render(wrap(<HumanMessagesTab node={humanNode} path={[humanNode]} />));
    expect(
      screen.queryByTestId("tab-human-messages-composer"),
    ).toBeNull();
    expect(
      screen.queryByTestId("tab-human-messages-composer-send"),
    ).toBeNull();
    expect(
      screen.queryByTestId("tab-human-messages-composer-input"),
    ).toBeNull();
  });
});

describe("HumanMessagesTab — populated thread list", () => {
  it("renders the timeline events when a canonical thread exists", () => {
    useThreadsMock.mockReturnValue({
      data: [
        {
          id: "t-1",
          summary: "Latest",
          lastActivity: "2026-04-30T10:00:00Z",
          status: "active",
          participants: [
            { address: `human:${HUMAN_ID}`, displayName: "Savas" },
            { address: "agent://ada", displayName: "ada" },
          ],
        },
      ],
      isLoading: false,
      error: null,
    });
    useThreadMock.mockReturnValue({
      data: {
        summary: {
          id: "t-1",
          status: "active",
          participants: [
            { address: `human:${HUMAN_ID}`, displayName: "Savas" },
            { address: "agent://ada", displayName: "ada" },
          ],
        },
        events: [
          {
            id: "e-1",
            eventType: "MessageReceived",
            source: { address: `human:${HUMAN_ID}`, displayName: "Savas" },
            from: { address: `human:${HUMAN_ID}`, displayName: "Savas" },
            timestamp: "2026-04-30T10:00:00Z",
            severity: "Info",
            summary: "hello",
            body: "hello team",
          },
        ],
      },
      isPending: false,
    });
    render(wrap(<HumanMessagesTab node={humanNode} path={[humanNode]} />));
    expect(screen.queryByTestId("tab-human-messages-empty")).toBeNull();
    expect(screen.getByTestId("conversation-event-e-1")).toBeInTheDocument();
  });

  it("picks the most-recently-active thread when more than one matches", () => {
    useThreadsMock.mockReturnValue({
      data: [
        {
          id: "older",
          lastActivity: "2026-04-01T00:00:00Z",
          participants: [
            { address: `human:${HUMAN_ID}`, displayName: "Savas" },
            { address: "agent://ada", displayName: "ada" },
          ],
        },
        {
          id: "newer",
          lastActivity: "2026-04-29T00:00:00Z",
          participants: [
            { address: `human:${HUMAN_ID}`, displayName: "Savas" },
            { address: "unit://engineering", displayName: "engineering" },
          ],
        },
      ],
      isLoading: false,
      error: null,
    });
    render(wrap(<HumanMessagesTab node={humanNode} path={[humanNode]} />));
    // ConversationView calls useThread internally with the picked id.
    expect(useThreadMock).toHaveBeenCalled();
    const lastCall = useThreadMock.mock.calls.at(-1);
    expect(lastCall?.[0]).toBe("newer");
  });
});

describe("HumanMessagesTab — routing (#2268)", () => {
  it("registers under (Human, Messages) so `?tab=Messages` resolves to this body", async () => {
    // Importing the module body fires `registerTab("Human", "Messages", ...)`
    // at top-level. The import at the top of this test file already
    // populates the registry — re-asserting the slot here documents the
    // routing contract: `?tab=Messages` on a Human page resolves via
    // `lookupTab("Human", "Messages")` and must return a real component.
    const { lookupTab } = await import("./index");
    const component = lookupTab("Human", "Messages");
    expect(component).not.toBeNull();
  });
});
