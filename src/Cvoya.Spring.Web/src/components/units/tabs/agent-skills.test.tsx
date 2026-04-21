import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { AgentNode } from "../aggregate";

vi.mock("@/lib/api/client", () => ({
  api: {
    getAgentSkills: vi.fn(async () => ({ skills: ["git", "grep"] })),
  },
}));

import AgentSkillsTab from "./agent-skills";

function renderWithClient(ui: React.ReactElement) {
  const qc = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  return render(
    <QueryClientProvider client={qc}>{ui}</QueryClientProvider>,
  );
}

describe("AgentSkillsTab", () => {
  it("renders the equipped skills", async () => {
    const node: AgentNode = {
      kind: "Agent",
      id: "ada",
      name: "Ada",
      status: "running",
    };
    renderWithClient(<AgentSkillsTab node={node} path={[node]} />);
    await waitFor(() =>
      expect(screen.getByTestId("tab-agent-skills")).toBeInTheDocument(),
    );
    expect(screen.getByText("git")).toBeInTheDocument();
    expect(screen.getByText("grep")).toBeInTheDocument();
  });
});
