/**
 * Tests for PackageDetailClient — Install button and inputs form.
 * ADR-0035 decision 11 / #1565.
 */

import { act, render, screen, waitFor, fireEvent } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ReactNode } from "react";

import type { PackageDetail, InstallStatusResponse } from "@/lib/api/types";

// ---- mocks ---------------------------------------------------------------

const getPackage = vi.fn<(name: string) => Promise<PackageDetail | null>>();
const installPackages = vi.fn<
  (targets: { packageName: string; inputs: Record<string, string> | null }[]) =>
    Promise<InstallStatusResponse>
>();

vi.mock("@/lib/api/client", () => ({
  api: {
    getPackage: (name: string) => getPackage(name),
    installPackages: (targets: unknown) => installPackages(targets as never),
  },
}));

const mockPush = vi.fn();
vi.mock("next/navigation", () => ({
  useRouter: () => ({ push: mockPush }),
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

// Import after mocks
import PackageDetailClient from "./package-detail-client";

// ---- helpers --------------------------------------------------------------

function makePackage(overrides?: Partial<PackageDetail>): PackageDetail {
  return {
    name: "my-pkg",
    description: "A test package",
    unitTemplates: [],
    agentTemplates: [],
    skills: [],
    connectors: [],
    workflows: [],
    ...overrides,
  };
}

function makeInstallStatus(
  overrides?: Partial<InstallStatusResponse>,
): InstallStatusResponse {
  return {
    installId: "aaaabbbb-0000-0000-0000-000000000001",
    status: "staging",
    packages: [{ packageName: "my-pkg", state: "staging", errorMessage: null }],
    startedAt: null,
    completedAt: null,
    error: null,
    ...overrides,
  };
}

function renderPage(name = "my-pkg") {
  const client = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0, staleTime: 0 },
    },
  });
  const Wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={client}>{children}</QueryClientProvider>
  );
  return render(<PackageDetailClient name={name} />, { wrapper: Wrapper });
}

// ---- tests ----------------------------------------------------------------

describe("PackageDetailClient — Install button", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockPush.mockReset();
  });

  it("renders Install button when package loads", async () => {
    getPackage.mockResolvedValue(makePackage());
    renderPage();
    await waitFor(() => {
      expect(screen.getByTestId("install-button")).toBeInTheDocument();
    });
  });

  it("renders Install button even when package has no templates", async () => {
    getPackage.mockResolvedValue(makePackage({ unitTemplates: [], agentTemplates: [] }));
    renderPage();
    await waitFor(() => {
      expect(screen.getByTestId("install-button")).toBeInTheDocument();
    });
  });

  it("clicking Install opens the inputs dialog", async () => {
    getPackage.mockResolvedValue(makePackage());
    renderPage();
    await waitFor(() => screen.getByTestId("install-button"));
    await act(async () => {
      fireEvent.click(screen.getByTestId("install-button"));
    });
    expect(screen.getByRole("dialog")).toBeInTheDocument();
  });

  it("dialog has 'Add input' button", async () => {
    getPackage.mockResolvedValue(makePackage());
    renderPage();
    await waitFor(() => screen.getByTestId("install-button"));
    await act(async () => {
      fireEvent.click(screen.getByTestId("install-button"));
    });
    expect(screen.getByRole("button", { name: /add input/i })).toBeInTheDocument();
  });

  it("clicking Add input adds a key/value row", async () => {
    getPackage.mockResolvedValue(makePackage());
    renderPage();
    await waitFor(() => screen.getByTestId("install-button"));
    await act(async () => {
      fireEvent.click(screen.getByTestId("install-button"));
    });
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: /add input/i }));
    });
    expect(screen.getByRole("textbox", { name: /input key 1/i })).toBeInTheDocument();
    expect(screen.getByRole("textbox", { name: /input value 1/i })).toBeInTheDocument();
  });

  it("submit without inputs sends empty inputs map", async () => {
    getPackage.mockResolvedValue(makePackage());
    installPackages.mockResolvedValue(makeInstallStatus());
    renderPage();
    await waitFor(() => screen.getByTestId("install-button"));
    await act(async () => {
      fireEvent.click(screen.getByTestId("install-button"));
    });
    await act(async () => {
      fireEvent.click(screen.getByTestId("install-submit-button"));
    });

    await waitFor(() => {
      expect(installPackages).toHaveBeenCalledWith([
        { packageName: "my-pkg", inputs: {} },
      ]);
    });
  });

  it("submit calls installPackages with filled inputs", async () => {
    getPackage.mockResolvedValue(makePackage());
    installPackages.mockResolvedValue(
      makeInstallStatus({ installId: "aaaabbbb-0000-0000-0000-000000000001" }),
    );
    renderPage();
    await waitFor(() => screen.getByTestId("install-button"));

    // Open dialog
    await act(async () => {
      fireEvent.click(screen.getByTestId("install-button"));
    });

    // Add a row
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: /add input/i }));
    });

    // Fill key and value
    const keyInput = screen.getByRole("textbox", { name: /input key 1/i });
    const valInput = screen.getByRole("textbox", { name: /input value 1/i });
    fireEvent.change(keyInput, { target: { value: "team_name" } });
    fireEvent.change(valInput, { target: { value: "Acme" } });

    await act(async () => {
      fireEvent.click(screen.getByTestId("install-submit-button"));
    });

    await waitFor(() => {
      expect(installPackages).toHaveBeenCalledWith([
        { packageName: "my-pkg", inputs: { team_name: "Acme" } },
      ]);
    });
  });

  it("successful submit redirects to /installs/<id>", async () => {
    getPackage.mockResolvedValue(makePackage());
    installPackages.mockResolvedValue(
      makeInstallStatus({ installId: "aaaabbbb-0000-0000-0000-000000000001" }),
    );
    renderPage();
    await waitFor(() => screen.getByTestId("install-button"));
    await act(async () => {
      fireEvent.click(screen.getByTestId("install-button"));
    });
    await act(async () => {
      fireEvent.click(screen.getByTestId("install-submit-button"));
    });

    await waitFor(() => {
      expect(mockPush).toHaveBeenCalledWith(
        "/installs/aaaabbbb-0000-0000-0000-000000000001",
      );
    });
  });

  it("API error shows error message in the dialog", async () => {
    getPackage.mockResolvedValue(makePackage());
    installPackages.mockRejectedValue(new Error("Name collision: my-pkg already exists"));
    renderPage();
    await waitFor(() => screen.getByTestId("install-button"));
    await act(async () => {
      fireEvent.click(screen.getByTestId("install-button"));
    });
    await act(async () => {
      fireEvent.click(screen.getByTestId("install-submit-button"));
    });

    await waitFor(() => {
      expect(screen.getByTestId("install-error")).toHaveTextContent(
        "Name collision",
      );
    });
  });
});
