import { render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ReactNode } from "react";

import type { PackageSummary } from "@/lib/api/types";

const listPackages = vi.fn<() => Promise<PackageSummary[]>>();

vi.mock("@/lib/api/client", () => ({
  api: {
    listPackages: () => listPackages(),
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

import SettingsPackagesPage from "./page";

function renderPage() {
  const client = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0, staleTime: 0 },
    },
  });
  const Wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={client}>{children}</QueryClientProvider>
  );
  return render(<SettingsPackagesPage />, { wrapper: Wrapper });
}

describe("SettingsPackagesPage", () => {
  beforeEach(() => {
    listPackages.mockReset();
  });

  it("renders the h1 landmark (re-exported from /packages)", async () => {
    listPackages.mockResolvedValue([]);
    renderPage();
    await waitFor(() => {
      expect(
        screen.getByRole("heading", { level: 1, name: /packages/i }),
      ).toBeInTheDocument();
    });
  });
});
