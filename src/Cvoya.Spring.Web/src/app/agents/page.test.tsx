import { fireEvent, render, screen, within } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, expect, it, vi, beforeEach } from "vitest";
import type { ReactNode } from "react";

import type { AgentResponse } from "@/lib/api/types";

// Mock next/link
vi.mock("next/link", () => ({
  default: ({
    href,
    children,
    ...rest
  }: { href: string; children: ReactNode } & Record<string, unknown>) => (
    <a href={href} {...rest}>
      {children}
    </a>
  ),
}));

const listAgentsMock = vi.fn<() => Promise<AgentResponse[]>>();
vi.mock("@/lib/api/client", () => ({
  api: new Proxy(
    { listAgents: () => listAgentsMock() },
    {
      get: (target, prop: string) => {
        if (prop in target) {
          // @ts-expect-error — dynamic proxy
          return () => target[prop]();
        }
        return () => Promise.reject(new Error(`api.${prop} not stubbed`));
      },
    },
  ),
}));

import AgentsPage from "./page";

function Wrapper({ children }: { children: ReactNode }) {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  return (
    <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
  );
}

function makeAgent(overrides: Partial<AgentResponse> = {}): AgentResponse {
  return {
    id: "agent-1",
    name: "agent-1",
    displayName: "Agent One",
    description: "Test agent",
    role: null,
    registeredAt: "2024-01-01T00:00:00Z",
    model: null,
    specialty: null,
    enabled: true,
    executionMode: "Auto",
    parentUnit: "unit-1",
    hostingMode: "ephemeral",
    initiativeLevel: "Passive",
    ...overrides,
  };
}

describe("AgentsPage", () => {
  beforeEach(() => {
    listAgentsMock.mockReset();
  });

  it("renders the page heading and filter bar", async () => {
    listAgentsMock.mockResolvedValue([]);
    render(<AgentsPage />, { wrapper: Wrapper });
    expect(screen.getByRole("heading", { name: /agents/i })).toBeInTheDocument();
    await screen.findByTestId("agents-filter-bar");
    expect(screen.getByTestId("agents-hosting-filter")).toBeInTheDocument();
    expect(screen.getByTestId("agents-initiative-filter")).toBeInTheDocument();
  });

  it("shows empty state when no agents", async () => {
    listAgentsMock.mockResolvedValue([]);
    render(<AgentsPage />, { wrapper: Wrapper });
    await screen.findByTestId("agents-empty");
    expect(screen.getByTestId("agents-empty")).toHaveTextContent(
      "No agents registered",
    );
  });

  it("renders agent cards when agents are returned", async () => {
    listAgentsMock.mockResolvedValue([
      makeAgent({ name: "ada", displayName: "Ada" }),
      makeAgent({ name: "grace", displayName: "Grace" }),
    ]);
    render(<AgentsPage />, { wrapper: Wrapper });
    await screen.findByTestId("agents-grid");
    expect(screen.getByTestId("agent-card-ada")).toBeInTheDocument();
    expect(screen.getByTestId("agent-card-grace")).toBeInTheDocument();
  });

  it("filters by hosting mode", async () => {
    listAgentsMock.mockResolvedValue([
      makeAgent({ name: "eph-agent", displayName: "Eph", hostingMode: "ephemeral" }),
      makeAgent({
        name: "per-agent",
        displayName: "Per",
        hostingMode: "persistent",
      }),
    ]);
    render(<AgentsPage />, { wrapper: Wrapper });
    await screen.findByTestId("agents-grid");

    fireEvent.change(screen.getByTestId("agents-hosting-filter"), {
      target: { value: "persistent" },
    });

    const grid = screen.getByTestId("agents-grid");
    expect(within(grid).queryByTestId("agent-card-eph-agent")).not.toBeInTheDocument();
    expect(within(grid).getByTestId("agent-card-per-agent")).toBeInTheDocument();
  });

  it("filters by initiative level", async () => {
    listAgentsMock.mockResolvedValue([
      makeAgent({
        name: "passive-agent",
        displayName: "Passive",
        initiativeLevel: "Passive",
      }),
      makeAgent({
        name: "auto-agent",
        displayName: "Auto",
        initiativeLevel: "Autonomous",
      }),
    ]);
    render(<AgentsPage />, { wrapper: Wrapper });
    await screen.findByTestId("agents-grid");

    fireEvent.change(screen.getByTestId("agents-initiative-filter"), {
      target: { value: "Autonomous" },
    });

    const grid = screen.getByTestId("agents-grid");
    expect(
      within(grid).queryByTestId("agent-card-passive-agent"),
    ).not.toBeInTheDocument();
    expect(within(grid).getByTestId("agent-card-auto-agent")).toBeInTheDocument();
  });

  it("shows no-match empty state when filters exclude all agents", async () => {
    listAgentsMock.mockResolvedValue([
      makeAgent({ name: "ada", displayName: "Ada", hostingMode: "ephemeral" }),
    ]);
    render(<AgentsPage />, { wrapper: Wrapper });
    await screen.findByTestId("agents-grid");

    fireEvent.change(screen.getByTestId("agents-hosting-filter"), {
      target: { value: "persistent" },
    });

    await screen.findByTestId("agents-empty");
    expect(screen.getByTestId("agents-empty")).toHaveTextContent(
      "No agents match the current filters",
    );
  });
});
