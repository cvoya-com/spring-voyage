// Tests for <UnitAgentMessagesView /> — the shared canonical-thread
// timeline + composer surface for the Unit / Agent Messages tab. The
// suite focuses on the ADR-0062 § 5 / #2826 Hat-banner contract: a thin
// chip strip above the timeline that identifies the receiving Hat for
// the canonical thread when the wire field is set, and is hidden for
// pure A2A threads.

import { render, screen } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import type { ReactNode } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { ThreadSummary } from "@/lib/api/types";

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
  // <MessageComposer> reads the caller's bound Hats so the from-selector
  // can render. Tests don't exercise that path; a stable empty list
  // keeps the selector hidden.
  useCallerHumans: () => ({ data: [], isLoading: false, isError: false }),
}));

vi.mock("@/lib/stream/use-thread-stream", () => ({
  useThreadStream: () => ({ connected: false, events: [] }),
}));

vi.mock("@/lib/api/client", async () => {
  const actual = await vi.importActual<typeof import("@/lib/api/client")>(
    "@/lib/api/client",
  );
  return {
    ...actual,
    api: {
      sendThreadMessage: vi.fn(),
      sendMessage: vi.fn(),
    },
  };
});

vi.mock("@/components/ui/toast", () => ({
  useToast: () => ({ toast: vi.fn() }),
}));

import { UnitAgentMessagesView } from "./unit-agent-messages-view";

function wrap(node: ReactNode) {
  const client = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0 },
      mutations: { retry: false },
    },
  });
  return <QueryClientProvider client={client}>{node}</QueryClientProvider>;
}

function makeThread(overrides: Partial<ThreadSummary> = {}): ThreadSummary {
  return {
    id: "thread-canon",
    participants: [
      {
        id: "11111111-1111-1111-1111-111111111111",
        address: "human:11111111111111111111111111111111",
        displayName: "savas",
      },
      {
        id: "22222222-2222-2222-2222-222222222222",
        address: "agent:22222222222222222222222222222222",
        displayName: "ada",
      },
    ],
    lastActivity: new Date().toISOString(),
    createdAt: new Date().toISOString(),
    eventCount: 1,
    origin: {
      id: "22222222-2222-2222-2222-222222222222",
      address: "agent:22222222222222222222222222222222",
      displayName: "ada",
    },
    summary: "Hello.",
    isArchived: false,
    recipientHumanId: null,
    recipientHumanDisplayName: null,
    ...overrides,
  };
}

beforeEach(() => {
  useThreadsMock.mockReset();
  useThreadMock.mockReset();
  useThreadMock.mockReturnValue({ data: null, isPending: false });
});

describe("UnitAgentMessagesView — Hat banner (ADR-0062 § 5, #2826)", () => {
  it("renders the Hat chip above the timeline when the canonical thread carries recipientHumanDisplayName", () => {
    useThreadsMock.mockReturnValue({
      data: [
        makeThread({
          id: "thread-with-hat",
          recipientHumanId: "33333333-3333-3333-3333-333333333333",
          recipientHumanDisplayName: "savas",
        }),
      ],
      isLoading: false,
      error: null,
    });

    render(
      wrap(
        <UnitAgentMessagesView
          targetScheme="agent"
          targetPath="ada"
          targetName="Ada"
          rootTestId="tab-agent-messages"
        />,
      ),
    );

    expect(
      screen.getByTestId("tab-agent-messages-hat-banner"),
    ).toBeInTheDocument();
    const chip = screen.getByTestId(
      "tab-agent-messages-hat-chip-thread-with-hat",
    );
    expect(chip).toHaveTextContent("As savas");
    expect(chip).toHaveAttribute("title", "Received as savas");
  });

  it("does not render the Hat banner when the canonical thread is pure A2A (no human recipient)", () => {
    useThreadsMock.mockReturnValue({
      data: [
        makeThread({
          id: "thread-a2a",
          recipientHumanId: null,
          recipientHumanDisplayName: null,
        }),
      ],
      isLoading: false,
      error: null,
    });

    render(
      wrap(
        <UnitAgentMessagesView
          targetScheme="unit"
          targetPath="engineering"
          targetName="Engineering"
          rootTestId="tab-unit-messages"
        />,
      ),
    );

    expect(
      screen.queryByTestId("tab-unit-messages-hat-banner"),
    ).not.toBeInTheDocument();
  });

  it("does not render the Hat banner when there is no canonical thread (empty list)", () => {
    useThreadsMock.mockReturnValue({
      data: [],
      isLoading: false,
      error: null,
    });

    render(
      wrap(
        <UnitAgentMessagesView
          targetScheme="agent"
          targetPath="ada"
          targetName="Ada"
          rootTestId="tab-agent-messages"
        />,
      ),
    );

    expect(
      screen.queryByTestId("tab-agent-messages-hat-banner"),
    ).not.toBeInTheDocument();
  });
});
