// Tests for the redesigned Unit Messages tab (#1459 / #1460).
//
// The tab now renders the {current human, unit} 1:1 engagement timeline
// inline (no master/detail list, no NewThreadDialog) with a persistent
// composer at the bottom that creates the thread implicitly when none
// exists yet.

import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import type { ReactNode } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { UnitNode } from "../aggregate";

vi.mock("next/navigation", () => ({
  useRouter: () => ({ replace: vi.fn(), push: vi.fn() }),
  usePathname: () => "/units",
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
vi.mock("@/lib/api/queries", () => ({
  useThreads: (filters: unknown, opts?: unknown) =>
    useThreadsMock(filters, opts),
  useThread: (id: string, opts?: unknown) => useThreadMock(id, opts),
}));

const sendThreadMessageMock = vi.fn();
const sendMessageMock = vi.fn();
vi.mock("@/lib/api/client", async () => {
  const actual = await vi.importActual<typeof import("@/lib/api/client")>(
    "@/lib/api/client",
  );
  return {
    ...actual,
    api: {
      sendThreadMessage: (id: string, body: unknown) =>
        sendThreadMessageMock(id, body),
      sendMessage: (body: unknown) => sendMessageMock(body),
    },
  };
});

const toastMock = vi.fn();
vi.mock("@/components/ui/toast", () => ({
  useToast: () => ({ toast: toastMock }),
}));

import UnitMessagesTab from "./unit-messages";

function wrap(node: ReactNode) {
  const client = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0 },
      mutations: { retry: false },
    },
  });
  return <QueryClientProvider client={client}>{node}</QueryClientProvider>;
}

const node: UnitNode = {
  kind: "Unit",
  id: "engineering",
  name: "Engineering",
  status: "running",
};

beforeEach(() => {
  useThreadsMock.mockReset();
  useThreadMock.mockReset();
  sendThreadMessageMock.mockReset();
  sendMessageMock.mockReset();
  toastMock.mockReset();
  useThreadMock.mockReturnValue({ data: null, isPending: false });
});

describe("UnitMessagesTab — single 1:1 engagement timeline (#1459)", () => {
  it("filters threads by unit id (no participant filter — #1472 fix)", () => {
    useThreadsMock.mockReturnValue({
      data: [],
      isLoading: false,
      error: null,
    });
    render(wrap(<UnitMessagesTab node={node} path={[node]} />));
    // #1472 fix: no participant filter — just the unit id.
    // (second arg is undefined because no options are passed)
    expect(useThreadsMock).toHaveBeenCalledWith({ unit: "engineering" }, undefined);
  });

  it("renders an empty-state hint and the composer when no thread exists", () => {
    useThreadsMock.mockReturnValue({
      data: [],
      isLoading: false,
      error: null,
    });
    render(wrap(<UnitMessagesTab node={node} path={[node]} />));
    expect(
      screen.getByTestId("tab-unit-messages-empty"),
    ).toHaveTextContent(/Engineering/);
    expect(screen.getByTestId("tab-unit-messages-composer")).toBeInTheDocument();
    // The legacy "+ New conversation" trigger and dialog are gone.
    expect(screen.queryByTestId("new-conversation-trigger")).toBeNull();
  });

  it("renders the timeline events when the canonical thread exists", () => {
    useThreadsMock.mockReturnValue({
      data: [
        {
          id: "t-1",
          summary: "Latest",
          lastActivity: "2026-04-30T10:00:00Z",
          status: "active",
          participants: [
            { address: "human://alice", displayName: "alice" },
            { address: "unit://engineering", displayName: "engineering" },
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
            { address: "human://alice", displayName: "alice" },
            { address: "unit://engineering", displayName: "engineering" },
          ],
        },
        events: [
          {
            id: "e-1",
            eventType: "MessageReceived",
            source: { address: "human://alice", displayName: "alice" },
            timestamp: "2026-04-30T10:00:00Z",
            severity: "Info",
            summary: "hello",
            body: "hello unit",
          },
        ],
      },
      isPending: false,
    });
    render(wrap(<UnitMessagesTab node={node} path={[node]} />));
    expect(screen.queryByTestId("tab-unit-messages-empty")).toBeNull();
    expect(screen.getByTestId("conversation-event-e-1")).toBeInTheDocument();
  });

  it("picks the most-recently-active thread when more than one matches", () => {
    useThreadsMock.mockReturnValue({
      data: [
        {
          id: "older",
          lastActivity: "2026-04-01T00:00:00Z",
          participants: ["human://alice", "unit://engineering"],
        },
        {
          id: "newer",
          lastActivity: "2026-04-29T00:00:00Z",
          participants: ["human://alice", "unit://engineering"],
        },
      ],
      isLoading: false,
      error: null,
    });
    render(wrap(<UnitMessagesTab node={node} path={[node]} />));
    expect(useThreadMock).toHaveBeenLastCalledWith(
      "newer",
      expect.objectContaining({ enabled: true }),
    );
  });
});

describe("UnitMessagesTab — inline composer (#1460)", () => {
  it("uses sendMessage to create a fresh thread when none exists yet", async () => {
    useThreadsMock.mockReturnValue({
      data: [],
      isLoading: false,
      error: null,
    });
    sendMessageMock.mockResolvedValue({ threadId: "t-new" });
    render(wrap(<UnitMessagesTab node={node} path={[node]} />));
    fireEvent.change(screen.getByTestId("tab-unit-messages-composer-input"), {
      target: { value: "Kick things off." },
    });
    fireEvent.click(screen.getByTestId("tab-unit-messages-composer-send"));
    await waitFor(() => {
      expect(sendMessageMock).toHaveBeenCalledWith({
        to: { scheme: "unit", path: "engineering" },
        type: "Domain",
        threadId: null,
        payload: "Kick things off.",
      });
    });
    expect(sendThreadMessageMock).not.toHaveBeenCalled();
  });

  it("appends to the existing thread via sendThreadMessage when one is live", async () => {
    useThreadsMock.mockReturnValue({
      data: [
        {
          id: "t-1",
          lastActivity: "2026-04-30T10:00:00Z",
          participants: ["human://alice", "unit://engineering"],
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
          participants: ["human://alice", "unit://engineering"],
        },
        events: [],
      },
      isPending: false,
    });
    sendThreadMessageMock.mockResolvedValue(undefined);
    render(wrap(<UnitMessagesTab node={node} path={[node]} />));
    fireEvent.change(screen.getByTestId("tab-unit-messages-composer-input"), {
      target: { value: "Reply." },
    });
    fireEvent.click(screen.getByTestId("tab-unit-messages-composer-send"));
    await waitFor(() => {
      expect(sendThreadMessageMock).toHaveBeenCalledWith("t-1", {
        to: { scheme: "unit", path: "engineering" },
        text: "Reply.",
      });
    });
    expect(sendMessageMock).not.toHaveBeenCalled();
  });

  it("disables Send when the textarea is empty", () => {
    useThreadsMock.mockReturnValue({
      data: [],
      isLoading: false,
      error: null,
    });
    render(wrap(<UnitMessagesTab node={node} path={[node]} />));
    expect(
      screen.getByTestId("tab-unit-messages-composer-send"),
    ).toBeDisabled();
  });
});
