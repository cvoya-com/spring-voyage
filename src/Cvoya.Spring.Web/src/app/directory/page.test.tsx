/**
 * Unit tests for the `/directory` tenant-wide expertise surface (#486).
 * The page fans out per-agent and per-unit expertise reads via TanStack
 * `useQueries`, then filters the flattened rows. These tests mock the
 * client so we can assert the UI shape without a live server.
 */

import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ReactNode } from "react";

import type {
  AgentResponse,
  ExpertiseDomainDto,
  UnitResponse,
} from "@/lib/api/types";

const listAgents = vi.fn<() => Promise<AgentResponse[]>>();
const listUnits = vi.fn<() => Promise<UnitResponse[]>>();
const getAgentExpertise =
  vi.fn<(id: string) => Promise<ExpertiseDomainDto[]>>();
const getUnitOwnExpertise =
  vi.fn<(id: string) => Promise<ExpertiseDomainDto[]>>();

vi.mock("@/lib/api/client", () => ({
  api: {
    listAgents: () => listAgents(),
    listUnits: () => listUnits(),
    getAgentExpertise: (id: string) => getAgentExpertise(id),
    getUnitOwnExpertise: (id: string) => getUnitOwnExpertise(id),
  },
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

import DirectoryPage from "./page";

function makeAgent(overrides: Partial<AgentResponse> = {}): AgentResponse {
  return {
    id: "actor-id",
    name: "ada",
    displayName: "Ada",
    description: "",
    role: null,
    registeredAt: new Date().toISOString(),
    model: null,
    specialty: null,
    enabled: true,
    executionMode: "Auto",
    parentUnit: null,
    ...overrides,
  } as AgentResponse;
}

function makeUnit(overrides: Partial<UnitResponse> = {}): UnitResponse {
  return {
    id: "unit-actor-id",
    name: "engineering",
    displayName: "Engineering",
    description: "",
    registeredAt: new Date().toISOString(),
    status: "Draft",
    model: null,
    color: null,
    ...overrides,
  } as UnitResponse;
}

function renderPage() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0 } },
  });
  return render(
    <QueryClientProvider client={client}>
      <DirectoryPage />
    </QueryClientProvider>,
  );
}

describe("/directory", () => {
  beforeEach(() => {
    listAgents.mockReset();
    listUnits.mockReset();
    getAgentExpertise.mockReset();
    getUnitOwnExpertise.mockReset();
  });

  it("renders the empty state when no entity declares expertise", async () => {
    listAgents.mockResolvedValue([makeAgent()]);
    listUnits.mockResolvedValue([makeUnit()]);
    getAgentExpertise.mockResolvedValue([]);
    getUnitOwnExpertise.mockResolvedValue([]);

    renderPage();

    await waitFor(() => {
      expect(
        screen.getByText(/No expertise declared/i),
      ).toBeInTheDocument();
    });
  });

  it("flattens agent and unit expertise into a single searchable list", async () => {
    listAgents.mockResolvedValue([
      makeAgent({ name: "ada", displayName: "Ada" }),
    ]);
    listUnits.mockResolvedValue([
      makeUnit({ name: "engineering", displayName: "Engineering" }),
    ]);
    getAgentExpertise.mockResolvedValue([
      { name: "python", level: "expert", description: "" },
    ]);
    getUnitOwnExpertise.mockResolvedValue([
      { name: "team-coordination", level: null, description: "" },
    ]);

    renderPage();

    await waitFor(() => {
      expect(screen.getByText("python")).toBeInTheDocument();
      expect(screen.getByText("team-coordination")).toBeInTheDocument();
    });

    // Each row deep-links to the owning agent or unit page.
    const agentLink = screen.getByRole("link", {
      name: /agent:\/\/ada/i,
    });
    expect(agentLink).toHaveAttribute("href", "/agents/ada");
    const unitLink = screen.getByRole("link", {
      name: /unit:\/\/engineering/i,
    });
    expect(unitLink).toHaveAttribute("href", "/units/engineering");
  });

  it("filters by free-text search", async () => {
    listAgents.mockResolvedValue([
      makeAgent({ name: "ada", displayName: "Ada" }),
    ]);
    listUnits.mockResolvedValue([]);
    getAgentExpertise.mockResolvedValue([
      { name: "python", level: "expert", description: "Backend" },
      { name: "rust", level: "intermediate", description: "Systems" },
    ]);

    renderPage();

    await waitFor(() => {
      expect(screen.getByText("python")).toBeInTheDocument();
    });

    fireEvent.change(screen.getByLabelText(/Search expertise/i), {
      target: { value: "rust" },
    });

    await waitFor(() => {
      expect(screen.queryByText("python")).toBeNull();
    });
    expect(screen.getByText("rust")).toBeInTheDocument();
  });

  it("filters by level", async () => {
    listAgents.mockResolvedValue([
      makeAgent({ name: "ada", displayName: "Ada" }),
    ]);
    listUnits.mockResolvedValue([]);
    getAgentExpertise.mockResolvedValue([
      { name: "python", level: "expert", description: "" },
      { name: "rust", level: "intermediate", description: "" },
    ]);

    renderPage();

    await waitFor(() => {
      expect(screen.getByText("python")).toBeInTheDocument();
    });

    fireEvent.change(screen.getByLabelText(/^Level$/i), {
      target: { value: "expert" },
    });

    await waitFor(() => {
      expect(screen.queryByText("rust")).toBeNull();
    });
    expect(screen.getByText("python")).toBeInTheDocument();
  });
});
