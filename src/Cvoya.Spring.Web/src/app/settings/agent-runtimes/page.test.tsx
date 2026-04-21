import { render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ReactNode } from "react";

import type {
  CredentialHealthResponse,
  InstalledAgentRuntimeResponse,
} from "@/lib/api/types";

const listAgentRuntimes =
  vi.fn<() => Promise<InstalledAgentRuntimeResponse[]>>();
const getAgentRuntimeCredentialHealth =
  vi.fn<(id: string) => Promise<CredentialHealthResponse | null>>();

vi.mock("@/lib/api/client", () => ({
  api: {
    listAgentRuntimes: () => listAgentRuntimes(),
    getAgentRuntimeCredentialHealth: (id: string) =>
      getAgentRuntimeCredentialHealth(id),
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

import SettingsAgentRuntimesPage from "./page";

function renderPage() {
  const client = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0, staleTime: 0 },
    },
  });
  const Wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={client}>{children}</QueryClientProvider>
  );
  return render(<SettingsAgentRuntimesPage />, { wrapper: Wrapper });
}

describe("SettingsAgentRuntimesPage", () => {
  beforeEach(() => {
    listAgentRuntimes.mockReset();
    getAgentRuntimeCredentialHealth.mockReset();
  });

  it("renders the h1 landmark (re-exported from /admin/agent-runtimes)", async () => {
    listAgentRuntimes.mockResolvedValue([]);
    renderPage();
    await waitFor(() => {
      expect(
        screen.getByRole("heading", { level: 1, name: /agent runtimes/i }),
      ).toBeInTheDocument();
    });
  });
});
