import { render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, expect, it, vi, beforeEach } from "vitest";
import type { ReactNode } from "react";

import AgentsPage from "./page";

const mockListAgents = vi.fn();
const mockSearchDirectory = vi.fn();
const mockReplace = vi.fn();
let mockSearchParams = new URLSearchParams("");

vi.mock("@/lib/api/client", () => ({
  api: {
    listAgents: (...args: unknown[]) => mockListAgents(...args),
    searchDirectory: (...args: unknown[]) => mockSearchDirectory(...args),
  },
}));

vi.mock("next/navigation", () => ({
  useRouter: () => ({ replace: mockReplace, push: vi.fn() }),
  useSearchParams: () => mockSearchParams,
}));

vi.mock("next/link", () => ({
  default: ({
    href,
    children,
    ...rest
  }: {
    href: string;
    children: React.ReactNode;
  } & Record<string, unknown>) => (
    <a href={href} {...rest}>
      {children}
    </a>
  ),
}));

function Wrapper({ children }: { children: ReactNode }) {
  const client = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0, staleTime: 0 },
    },
  });
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

const agents = [
  {
    id: "1",
    name: "ada",
    displayName: "Ada",
    description: "Backend engineer",
    role: "backend",
    registeredAt: "2026-04-01T00:00:00Z",
    model: "gpt-4o",
    specialty: null,
    enabled: true,
    executionMode: "Auto",
    parentUnit: "engineering",
  },
  {
    id: "2",
    name: "grace",
    displayName: "Grace",
    description: "Frontend engineer",
    role: "frontend",
    registeredAt: "2026-04-02T00:00:00Z",
    model: "gpt-4o",
    specialty: null,
    enabled: true,
    executionMode: "Auto",
    parentUnit: "engineering",
  },
  {
    id: "3",
    name: "amelia",
    displayName: "Amelia",
    description: "Researcher",
    role: "researcher",
    registeredAt: "2026-04-03T00:00:00Z",
    model: "gpt-4o",
    specialty: null,
    enabled: false,
    executionMode: "OnDemand",
    parentUnit: null,
  },
];

describe("AgentsPage", () => {
  beforeEach(() => {
    mockListAgents.mockReset();
    mockSearchDirectory.mockReset();
    mockReplace.mockReset();
    mockSearchParams = new URLSearchParams("");
    mockListAgents.mockResolvedValue(agents);
    mockSearchDirectory.mockResolvedValue({
      hits: [],
      totalCount: 0,
      limit: 50,
      offset: 0,
    });
  });

  it("renders one card per agent with cross-links to lens quick actions", async () => {
    render(
      <Wrapper>
        <AgentsPage />
      </Wrapper>,
    );
    await waitFor(() => {
      expect(screen.getByTestId("agent-card-ada")).toBeInTheDocument();
      expect(screen.getByTestId("agent-card-grace")).toBeInTheDocument();
      expect(screen.getByTestId("agent-card-amelia")).toBeInTheDocument();
    });

    expect(
      screen.getByTestId("agent-lens-conversation-ada"),
    ).toHaveAttribute(
      "href",
      "/conversations?participant=agent%3A%2F%2Fada",
    );
    expect(screen.getByTestId("agent-lens-deployment-ada")).toHaveAttribute(
      "href",
      "/agents/ada#deployment",
    );
  });

  it("filters by search text against name, display name, and role", async () => {
    mockSearchParams = new URLSearchParams("q=front");
    render(
      <Wrapper>
        <AgentsPage />
      </Wrapper>,
    );
    await waitFor(() => {
      expect(screen.getByTestId("agent-card-grace")).toBeInTheDocument();
    });
    expect(screen.queryByTestId("agent-card-ada")).toBeNull();
    expect(screen.queryByTestId("agent-card-amelia")).toBeNull();
  });

  it("filters by owning unit", async () => {
    mockSearchParams = new URLSearchParams("unit=engineering");
    render(
      <Wrapper>
        <AgentsPage />
      </Wrapper>,
    );
    await waitFor(() => {
      expect(screen.getByTestId("agent-card-ada")).toBeInTheDocument();
      expect(screen.getByTestId("agent-card-grace")).toBeInTheDocument();
    });
    expect(screen.queryByTestId("agent-card-amelia")).toBeNull();
  });

  it("narrows to disabled agents when the status filter is applied", async () => {
    mockSearchParams = new URLSearchParams("status=disabled");
    render(
      <Wrapper>
        <AgentsPage />
      </Wrapper>,
    );
    await waitFor(() => {
      expect(screen.getByTestId("agent-card-amelia")).toBeInTheDocument();
    });
    expect(screen.queryByTestId("agent-card-ada")).toBeNull();
    expect(screen.queryByTestId("agent-card-grace")).toBeNull();
  });

  it("applies the expertise filter via the directory search endpoint", async () => {
    mockSearchDirectory.mockResolvedValue({
      hits: [
        {
          slug: "python",
          domain: { name: "python", description: null, level: null },
          owner: { scheme: "agent", path: "ada" },
          ownerDisplayName: "Ada",
          aggregatingUnit: null,
          typedContract: false,
          score: 1.0,
          matchReason: "exact",
          ancestorChain: [],
          projectionPaths: [],
        },
      ],
      totalCount: 1,
      limit: 200,
      offset: 0,
    });
    mockSearchParams = new URLSearchParams("expertise=python");
    render(
      <Wrapper>
        <AgentsPage />
      </Wrapper>,
    );
    await waitFor(() => {
      expect(screen.getByTestId("agent-card-ada")).toBeInTheDocument();
    });
    expect(screen.queryByTestId("agent-card-grace")).toBeNull();
    expect(screen.queryByTestId("agent-card-amelia")).toBeNull();
    expect(mockSearchDirectory).toHaveBeenCalledWith(
      expect.objectContaining({ text: "python" }),
    );
  });

  it("groups agents by unit when the grouping toggle is set to 'unit'", async () => {
    mockSearchParams = new URLSearchParams("group=unit");
    render(
      <Wrapper>
        <AgentsPage />
      </Wrapper>,
    );
    await waitFor(() => {
      expect(
        screen.getByTestId("agents-bucket-engineering"),
      ).toBeInTheDocument();
      expect(
        screen.getByTestId("agents-bucket-__unassigned__"),
      ).toBeInTheDocument();
    });
  });

  it("renders the search-empty state with a link to the directory", async () => {
    mockSearchParams = new URLSearchParams("q=zzzz");
    render(
      <Wrapper>
        <AgentsPage />
      </Wrapper>,
    );
    await waitFor(() => {
      expect(screen.getByTestId("agents-search-empty")).toBeInTheDocument();
    });
  });

  it("renders the fleet-empty state when no agents exist yet", async () => {
    mockListAgents.mockResolvedValue([]);
    render(
      <Wrapper>
        <AgentsPage />
      </Wrapper>,
    );
    await waitFor(() => {
      expect(screen.getByTestId("agents-empty")).toBeInTheDocument();
    });
    expect(screen.getByText("No agents yet")).toBeInTheDocument();
  });
});
