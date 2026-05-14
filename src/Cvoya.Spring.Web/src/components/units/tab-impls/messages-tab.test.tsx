// Tests for the unified Messages tab (#2256, umbrella #2252).
//
// Verifies that the kind→scheme mapping is correct for both supported
// subjects (Unit, Agent): the threads filter and outbound recipient must
// key on the right address scheme so the timeline + composer behave
// identically to the per-subject tabs they replace. Tenant is not a
// supported kind here (Messages does-not-apply on Tenant — see
// docs/design/canonical-tabs.md § 1 / § 4).

import { render, screen } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import type { ReactNode } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";

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

import { MessagesTab } from "./messages-tab";

function wrap(node: ReactNode) {
  const client = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0 },
      mutations: { retry: false },
    },
  });
  return <QueryClientProvider client={client}>{node}</QueryClientProvider>;
}

beforeEach(() => {
  useThreadsMock.mockReset();
  useThreadMock.mockReset();
  useThreadMock.mockReturnValue({ data: null, isPending: false });
});

describe("MessagesTab — unified kind→scheme mapping (#2256)", () => {
  it("filters threads by unit address scheme when kind = Unit", () => {
    useThreadsMock.mockReturnValue({
      data: [],
      isLoading: false,
      error: null,
    });
    render(
      wrap(<MessagesTab kind="Unit" id="engineering" name="Engineering" />),
    );
    expect(useThreadsMock).toHaveBeenCalledWith(
      { unit: "engineering" },
      undefined,
    );
  });

  it("filters threads by agent address scheme when kind = Agent", () => {
    useThreadsMock.mockReturnValue({
      data: [],
      isLoading: false,
      error: null,
    });
    render(wrap(<MessagesTab kind="Agent" id="ada" name="Ada" />));
    expect(useThreadsMock).toHaveBeenCalledWith({ agent: "ada" }, undefined);
  });

  it("uses a kind-scoped rootTestId on the container so per-subject", () => {
    useThreadsMock.mockReturnValue({
      data: [],
      isLoading: false,
      error: null,
    });
    const { rerender } = render(
      wrap(<MessagesTab kind="Unit" id="engineering" name="Engineering" />),
    );
    expect(screen.getByTestId("tab-unit-messages")).toBeInTheDocument();

    rerender(wrap(<MessagesTab kind="Agent" id="ada" name="Ada" />));
    expect(screen.getByTestId("tab-agent-messages")).toBeInTheDocument();
  });

  it("renders the empty state with the subject display name", () => {
    useThreadsMock.mockReturnValue({
      data: [],
      isLoading: false,
      error: null,
    });
    render(wrap(<MessagesTab kind="Agent" id="ada" name="Ada" />));
    expect(screen.getByTestId("tab-agent-messages-empty")).toHaveTextContent(
      /Ada/,
    );
  });
});
