import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import {
  fireEvent,
  render,
  screen,
  waitFor,
} from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { AgentNode } from "../aggregate";

const getAgentSkills = vi.fn();
const setAgentSkills = vi.fn();
const listSkills = vi.fn();

vi.mock("@/lib/api/client", () => ({
  api: {
    getAgentSkills: (...args: unknown[]) => getAgentSkills(...args),
    setAgentSkills: (...args: unknown[]) => setAgentSkills(...args),
    listSkills: (...args: unknown[]) => listSkills(...args),
  },
}));

const toastMock = vi.fn();
vi.mock("@/components/ui/toast", () => ({
  useToast: () => ({ toast: toastMock }),
}));

import AgentSkillsTab from "./agent-skills";

function renderWithClient(ui: React.ReactElement) {
  const qc = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0 },
      mutations: { retry: false },
    },
  });
  return render(
    <QueryClientProvider client={qc}>{ui}</QueryClientProvider>,
  );
}

const node: AgentNode = {
  kind: "Agent",
  id: "ada",
  name: "Ada",
  status: "running",
};

describe("AgentSkillsTab", () => {
  beforeEach(() => {
    getAgentSkills.mockReset();
    setAgentSkills.mockReset();
    listSkills.mockReset();
    toastMock.mockReset();
  });

  it("renders the equipped skills", async () => {
    getAgentSkills.mockResolvedValue({ skills: ["git", "grep"] });
    listSkills.mockResolvedValue([]);

    renderWithClient(<AgentSkillsTab node={node} path={[node]} />);

    await waitFor(() =>
      expect(screen.getByTestId("tab-agent-skills")).toBeInTheDocument(),
    );
    expect(screen.getByText("git")).toBeInTheDocument();
    expect(screen.getByText("grep")).toBeInTheDocument();
  });

  it("adds a skill by selecting it from the catalog combobox", async () => {
    getAgentSkills.mockResolvedValue({ skills: ["git"] });
    listSkills.mockResolvedValue([
      { name: "git", registry: "builtin", description: null, hasTools: false },
      {
        name: "search",
        registry: "builtin",
        description: "Search the web",
        hasTools: false,
      },
      {
        name: "summarize",
        registry: "builtin",
        description: null,
        hasTools: false,
      },
    ]);
    setAgentSkills.mockResolvedValue({ skills: ["git", "search"] });

    renderWithClient(<AgentSkillsTab node={node} path={[node]} />);

    const combobox = (await screen.findByTestId(
      "tab-agent-skills-add",
    )) as HTMLSelectElement;

    // Wait for the catalog to populate (equipped skills are filtered out,
    // so "git" should not be in the dropdown but "search" should).
    await waitFor(() => {
      expect(combobox.querySelector('option[value="search"]')).not.toBeNull();
    });
    expect(combobox.querySelector('option[value="git"]')).toBeNull();

    fireEvent.change(combobox, { target: { value: "search" } });

    await waitFor(() => {
      expect(setAgentSkills).toHaveBeenCalledWith("ada", ["git", "search"]);
    });
    expect(toastMock).not.toHaveBeenCalled();
  });

  it("removes a skill when the chip's remove button is clicked", async () => {
    getAgentSkills.mockResolvedValue({ skills: ["git", "grep"] });
    listSkills.mockResolvedValue([]);
    setAgentSkills.mockResolvedValue({ skills: ["grep"] });

    renderWithClient(<AgentSkillsTab node={node} path={[node]} />);

    const removeBtn = await screen.findByTestId("tab-agent-skills-remove-git");
    fireEvent.click(removeBtn);

    await waitFor(() => {
      expect(setAgentSkills).toHaveBeenCalledWith("ada", ["grep"]);
    });
    expect(toastMock).not.toHaveBeenCalled();
  });

  it("surfaces a destructive toast when the mutation fails", async () => {
    getAgentSkills.mockResolvedValue({ skills: ["git"] });
    listSkills.mockResolvedValue([
      {
        name: "search",
        registry: "builtin",
        description: null,
        hasTools: false,
      },
    ]);
    setAgentSkills.mockRejectedValue(new Error("boom"));

    renderWithClient(<AgentSkillsTab node={node} path={[node]} />);

    const combobox = (await screen.findByTestId(
      "tab-agent-skills-add",
    )) as HTMLSelectElement;
    await waitFor(() => {
      expect(combobox.querySelector('option[value="search"]')).not.toBeNull();
    });

    fireEvent.change(combobox, { target: { value: "search" } });

    await waitFor(() => {
      expect(toastMock).toHaveBeenCalledWith(
        expect.objectContaining({
          title: "Failed to add skill",
          variant: "destructive",
        }),
      );
    });
  });
});
