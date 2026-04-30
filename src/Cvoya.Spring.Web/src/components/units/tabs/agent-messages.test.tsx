// Tests for the redesigned Agent Messages tab (#1459 / #1460).
// Mirrors `unit-messages.test.tsx`; only the target scheme/path differ.

import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import type { ReactNode } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { AgentNode } from "../aggregate";

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

const useCurrentUserMock = vi.fn();
const useThreadsMock = vi.fn();
const useThreadMock = vi.fn();
vi.mock("@/lib/api/queries", () => ({
  useCurrentUser: () => useCurrentUserMock(),
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

import AgentMessagesTab from "./agent-messages";

function wrap(node: ReactNode) {
  const client = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0 },
      mutations: { retry: false },
    },
  });
  return <QueryClientProvider client={client}>{node}</QueryClientProvider>;
}

const node: AgentNode = {
  kind: "Agent",
  id: "ada",
  name: "Ada",
  status: "running",
};

beforeEach(() => {
  useCurrentUserMock.mockReset();
  useThreadsMock.mockReset();
  useThreadMock.mockReset();
  sendThreadMessageMock.mockReset();
  sendMessageMock.mockReset();
  toastMock.mockReset();
  useCurrentUserMock.mockReturnValue({
    data: { address: "human://alice" },
    isPending: false,
  });
  useThreadMock.mockReturnValue({ data: null, isPending: false });
});

describe("AgentMessagesTab — single 1:1 engagement timeline (#1459)", () => {
  it("filters threads by both agent id and the current human's address", () => {
    useThreadsMock.mockReturnValue({
      data: [],
      isLoading: false,
      error: null,
    });
    render(wrap(<AgentMessagesTab node={node} path={[node]} />));
    expect(useThreadsMock).toHaveBeenCalledWith(
      { agent: "ada", participant: "human://alice" },
      expect.any(Object),
    );
  });

  it("renders the empty-state hint and the composer when no thread exists", () => {
    useThreadsMock.mockReturnValue({
      data: [],
      isLoading: false,
      error: null,
    });
    render(wrap(<AgentMessagesTab node={node} path={[node]} />));
    expect(screen.getByTestId("tab-agent-messages-empty")).toHaveTextContent(
      /Ada/,
    );
    expect(screen.getByTestId("tab-agent-messages-composer")).toBeInTheDocument();
    expect(screen.queryByTestId("new-conversation-trigger")).toBeNull();
  });
});

describe("AgentMessagesTab — inline composer (#1460)", () => {
  it("uses sendMessage to create a fresh thread when none exists yet", async () => {
    useThreadsMock.mockReturnValue({
      data: [],
      isLoading: false,
      error: null,
    });
    sendMessageMock.mockResolvedValue({ threadId: "t-new" });
    render(wrap(<AgentMessagesTab node={node} path={[node]} />));
    fireEvent.change(screen.getByTestId("tab-agent-messages-composer-input"), {
      target: { value: "Hi Ada." },
    });
    fireEvent.click(screen.getByTestId("tab-agent-messages-composer-send"));
    await waitFor(() => {
      expect(sendMessageMock).toHaveBeenCalledWith({
        to: { scheme: "agent", path: "ada" },
        type: "Domain",
        threadId: null,
        payload: "Hi Ada.",
      });
    });
  });

  it("appends to the existing thread when one is live", async () => {
    useThreadsMock.mockReturnValue({
      data: [
        {
          id: "t-7",
          lastActivity: "2026-04-30T10:00:00Z",
          participants: ["human://alice", "agent://ada"],
        },
      ],
      isLoading: false,
      error: null,
    });
    useThreadMock.mockReturnValue({
      data: {
        summary: {
          id: "t-7",
          status: "active",
          participants: ["human://alice", "agent://ada"],
        },
        events: [],
      },
      isPending: false,
    });
    sendThreadMessageMock.mockResolvedValue(undefined);
    render(wrap(<AgentMessagesTab node={node} path={[node]} />));
    fireEvent.change(screen.getByTestId("tab-agent-messages-composer-input"), {
      target: { value: "follow-up" },
    });
    fireEvent.click(screen.getByTestId("tab-agent-messages-composer-send"));
    await waitFor(() => {
      expect(sendThreadMessageMock).toHaveBeenCalledWith("t-7", {
        to: { scheme: "agent", path: "ada" },
        text: "follow-up",
      });
    });
  });
});
