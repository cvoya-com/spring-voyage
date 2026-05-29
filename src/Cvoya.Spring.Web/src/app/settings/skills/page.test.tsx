import { render, screen, waitFor, within } from "@testing-library/react";
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
      } satisfies SkillCatalogEntry,
      {
        name: "summarize",
        registry: "builtin",
        description: "",
      } satisfies SkillCatalogEntry,
      {
        name: "slack.post",
        registry: "connectors",
        description: "Post to Slack",
      } satisfies SkillCatalogEntry,
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

    // #2890: assert *which* group each skill lands under, not merely that the
    // names render somewhere. The prior test queried the whole document, so a
    // regression that filed `slack.post` under `builtin` (or dropped grouping
    // entirely) stayed green.
    const builtin = within(
      screen.getByTestId("settings-skills-registry-builtin"),
    );
    const connectors = within(
      screen.getByTestId("settings-skills-registry-connectors"),
    );
    expect(builtin.getByText("search")).toBeInTheDocument();
    expect(builtin.getByText("summarize")).toBeInTheDocument();
    expect(connectors.getByText("slack.post")).toBeInTheDocument();
    // ...and not cross-filed under the other registry.
    expect(builtin.queryByText("slack.post")).toBeNull();
    expect(connectors.queryByText("search")).toBeNull();
  });
});
