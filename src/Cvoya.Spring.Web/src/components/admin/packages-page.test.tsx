/**
 * Tests for PackagesListPage — Browse / Upload stub (#1565).
 */

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

import PackagesListPage from "./packages-page";

function renderPage() {
  const client = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0, staleTime: 0 },
    },
  });
  const Wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={client}>{children}</QueryClientProvider>
  );
  return render(<PackagesListPage />, { wrapper: Wrapper });
}

describe("PackagesListPage — Browse / Upload stub", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    listPackages.mockResolvedValue([]);
  });

  it("renders the Browse / Upload card", async () => {
    renderPage();
    await waitFor(() => {
      expect(screen.getByTestId("browse-upload-stub")).toBeInTheDocument();
    });
  });

  it("renders the file picker input accepting package files", async () => {
    renderPage();
    await waitFor(() => {
      const input = screen.getByTestId("browse-file-input") as HTMLInputElement;
      expect(input).toBeInTheDocument();
      expect(input.getAttribute("accept")).toContain(".yaml");
    });
  });

  it("Upload button is disabled", async () => {
    renderPage();
    await waitFor(() => {
      expect(screen.getByTestId("browse-upload-button")).toBeDisabled();
    });
  });

  it("renders CLI hint with spring package install command", async () => {
    renderPage();
    await waitFor(() => {
      const hint = screen.getByTestId("browse-cli-hint");
      expect(hint).toBeInTheDocument();
      expect(hint.textContent).toContain("spring package install --file");
    });
  });

  it("renders the existing packages heading", async () => {
    renderPage();
    await waitFor(() => {
      expect(
        screen.getByRole("heading", { level: 1, name: /packages/i }),
      ).toBeInTheDocument();
    });
  });
});
