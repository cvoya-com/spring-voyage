// Tests for <UnitAgentMessagesView /> — the shared canonical-thread
// timeline + composer surface for the Unit / Agent Messages tab. The
// suite focuses on the ADR-0062 § 5 / #2826 Hat-banner contract: a thin
// chip strip above the timeline that identifies the receiving Hat for
// the canonical thread when the wire field is set, and is hidden for
// pure A2A threads.

import { fireEvent, render, screen } from "@testing-library/react";
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

  it("renders the disambiguated label when the canonical thread carries it (#2829)", () => {
    useThreadsMock.mockReturnValue({
      data: [
        makeThread({
          id: "thread-disambig",
          recipientHumanId: "44444444-4444-4444-4444-444444444444",
          recipientHumanDisplayName: "Bob",
          recipientHumanDisambiguatedLabel: "Bob — designer",
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

    const chip = screen.getByTestId(
      "tab-agent-messages-hat-chip-thread-disambig",
    );
    expect(chip).toHaveTextContent("As Bob — designer");
    expect(chip).toHaveAttribute("title", "Received as Bob — designer");
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

// ---------------------------------------------------------------------------
// Thread switcher (#2885)
// ---------------------------------------------------------------------------
//
// When the host (unit / agent) participates in 2+ threads, a compact chip
// row appears above the timeline so the operator can pick which thread is
// rendered inline. The default selection mirrors the pre-switcher
// behaviour: `pickCanonicalThread()` — the most-recently-active.

// Stable participant refs reused across the switcher tests. The wire
// `ParticipantRef` requires `id` post-#2082, so we supply it even though
// the switcher only reads `address` + `displayName`.
const SAVAS = {
  id: "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
  address: "human://savas",
  displayName: "savas",
};
const ADA = {
  id: "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
  address: "agent://ada",
  displayName: "ada",
};
const BOB = {
  id: "cccccccc-cccc-cccc-cccc-cccccccccccc",
  address: "agent://bob",
  displayName: "bob",
};

describe("UnitAgentMessagesView — thread switcher (#2885)", () => {
  it("does not render the switcher when only one thread matches", () => {
    useThreadsMock.mockReturnValue({
      data: [makeThread({ id: "thread-single" })],
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
      screen.queryByTestId("tab-agent-messages-thread-switcher"),
    ).not.toBeInTheDocument();
  });

  it("does not render the switcher when no threads match (empty list)", () => {
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
      screen.queryByTestId("tab-agent-messages-thread-switcher"),
    ).not.toBeInTheDocument();
  });

  it("renders one switcher chip per thread when 2+ threads match, defaulting to the most-recently-active", () => {
    useThreadsMock.mockReturnValue({
      data: [
        makeThread({
          id: "thread-older",
          lastActivity: "2026-04-01T00:00:00Z",
          participants: [SAVAS, ADA],
        }),
        makeThread({
          id: "thread-newer",
          lastActivity: "2026-04-30T00:00:00Z",
          participants: [SAVAS, ADA, BOB],
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

    const switcher = screen.getByTestId("tab-agent-messages-thread-switcher");
    expect(switcher).toBeInTheDocument();

    const newer = screen.getByTestId(
      "tab-agent-messages-thread-switcher-item-thread-newer",
    );
    const older = screen.getByTestId(
      "tab-agent-messages-thread-switcher-item-thread-older",
    );
    expect(newer).toBeInTheDocument();
    expect(older).toBeInTheDocument();

    // Default selection is the most-recently-active thread.
    expect(newer).toHaveAttribute("aria-selected", "true");
    expect(older).toHaveAttribute("aria-selected", "false");

    // The default thread id is forwarded to `useThread`.
    expect(useThreadMock).toHaveBeenLastCalledWith(
      "thread-newer",
      expect.objectContaining({ enabled: true }),
    );
  });

  it("labels each chip by the participants other than the hosting node", () => {
    useThreadsMock.mockReturnValue({
      data: [
        makeThread({
          id: "thread-1on1",
          lastActivity: "2026-04-01T00:00:00Z",
          participants: [SAVAS, ADA],
        }),
        makeThread({
          id: "thread-multi",
          lastActivity: "2026-04-30T00:00:00Z",
          participants: [SAVAS, ADA, BOB],
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

    // Host ("ada") is excluded; everyone else is listed.
    expect(
      screen.getByTestId("tab-agent-messages-thread-switcher-item-thread-1on1"),
    ).toHaveTextContent("savas");
    const multi = screen.getByTestId(
      "tab-agent-messages-thread-switcher-item-thread-multi",
    );
    expect(multi).toHaveTextContent("savas");
    expect(multi).toHaveTextContent("bob");
    expect(multi).not.toHaveTextContent(/\bada\b/);
  });

  it("switches the rendered conversation when a non-selected chip is clicked", () => {
    useThreadsMock.mockReturnValue({
      data: [
        makeThread({
          id: "thread-older",
          lastActivity: "2026-04-01T00:00:00Z",
          participants: [SAVAS, ADA],
        }),
        makeThread({
          id: "thread-newer",
          lastActivity: "2026-04-30T00:00:00Z",
          participants: [SAVAS, ADA, BOB],
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

    // Sanity: default selection is the most-recent thread.
    expect(useThreadMock).toHaveBeenLastCalledWith(
      "thread-newer",
      expect.objectContaining({ enabled: true }),
    );

    fireEvent.click(
      screen.getByTestId(
        "tab-agent-messages-thread-switcher-item-thread-older",
      ),
    );

    // After the click the older thread becomes selected and is the one
    // forwarded to `useThread`.
    expect(useThreadMock).toHaveBeenLastCalledWith(
      "thread-older",
      expect.objectContaining({ enabled: true }),
    );

    const older = screen.getByTestId(
      "tab-agent-messages-thread-switcher-item-thread-older",
    );
    const newer = screen.getByTestId(
      "tab-agent-messages-thread-switcher-item-thread-newer",
    );
    expect(older).toHaveAttribute("aria-selected", "true");
    expect(newer).toHaveAttribute("aria-selected", "false");
  });

  it("exposes the switcher with tab roles and is keyboard-activatable (Enter / Space)", () => {
    useThreadsMock.mockReturnValue({
      data: [
        makeThread({
          id: "thread-older",
          lastActivity: "2026-04-01T00:00:00Z",
          participants: [SAVAS, ADA],
        }),
        makeThread({
          id: "thread-newer",
          lastActivity: "2026-04-30T00:00:00Z",
          participants: [SAVAS, ADA, BOB],
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

    // The container is a tablist with the right accessible name.
    const switcher = screen.getByRole("tablist", {
      name: /switch conversation/i,
    });
    expect(switcher).toBeInTheDocument();

    // Each entry is a real <button role="tab"> — keyboard activation
    // (Enter / Space) is handled by the browser natively. We assert the
    // role + that activation fires the click handler (vitest dispatches
    // a click on Enter via the button element semantics).
    const tabs = screen.getAllByRole("tab");
    expect(tabs).toHaveLength(2);
    for (const t of tabs) {
      expect(t.tagName).toBe("BUTTON");
      expect(t).toHaveAttribute("type", "button");
    }

    // Programmatic keyboard activation — fire the same `click` the
    // browser would dispatch on Enter / Space when the button is
    // focused.
    const older = screen.getByTestId(
      "tab-agent-messages-thread-switcher-item-thread-older",
    );
    older.focus();
    expect(document.activeElement).toBe(older);
    fireEvent.click(older);

    expect(useThreadMock).toHaveBeenLastCalledWith(
      "thread-older",
      expect.objectContaining({ enabled: true }),
    );
  });
});
