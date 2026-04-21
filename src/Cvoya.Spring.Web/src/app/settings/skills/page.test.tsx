import { render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ReactNode } from "react";

import type { SkillCatalogEntry } from "@/lib/api/types";

const listSkills = vi.fn<() => Promise<SkillCatalogEntry[]>>();

vi.mock("@/lib/api/client", () => ({
  api: {
    listSkills: () => listSkills(),
  },
}));

import SettingsSkillsPage from "./page";

function renderPage() {
  const client = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0, staleTime: 0 },
    },
  });
  const Wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={client}>{children}</QueryClientProvider>
  );
  return render(<SettingsSkillsPage />, { wrapper: Wrapper });
}

describe("SettingsSkillsPage", () => {
  beforeEach(() => {
    listSkills.mockReset();
  });

  it("renders the h1 landmark", async () => {
    listSkills.mockResolvedValue([]);
    renderPage();
    expect(
      await screen.findByRole("heading", { level: 1, name: /skills/i }),
    ).toBeInTheDocument();
  });

  it("groups skills by registry", async () => {
    listSkills.mockResolvedValue([
      {
        name: "search",
        registry: "builtin",
        description: "Search the web",
        hasTools: false,
      } as SkillCatalogEntry,
      {
        name: "summarize",
        registry: "builtin",
        description: null,
        hasTools: false,
      } as SkillCatalogEntry,
      {
        name: "slack.post",
        registry: "connectors",
        description: "Post to Slack",
        hasTools: true,
      } as SkillCatalogEntry,
    ]);

    renderPage();

    await waitFor(() => {
      expect(
        screen.getByTestId("settings-skills-registry-builtin"),
      ).toBeInTheDocument();
    });
    expect(
      screen.getByTestId("settings-skills-registry-connectors"),
    ).toBeInTheDocument();
    expect(screen.getByText("search")).toBeInTheDocument();
    expect(screen.getByText("summarize")).toBeInTheDocument();
    expect(screen.getByText("slack.post")).toBeInTheDocument();
  });
});
