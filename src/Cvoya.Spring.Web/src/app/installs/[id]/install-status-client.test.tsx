/**
 * Tests for InstallStatusClient — staging / active / failed states,
 * retry, and abort. ADR-0035 decision 11 / #1565.
 */

import { act, render, screen, waitFor, fireEvent } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ReactNode } from "react";

import type { InstallStatusResponse } from "@/lib/api/types";

// ---- mocks ---------------------------------------------------------------

const getInstallStatus = vi.fn<
  (id: string) => Promise<InstallStatusResponse | null>
>();
const abortInstall = vi.fn<(id: string) => Promise<void>>();

vi.mock("@/lib/api/client", () => ({
  api: {
    getInstallStatus: (id: string) => getInstallStatus(id),
    abortInstall: (id: string) => abortInstall(id),
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

import InstallStatusClient from "./install-status-client";

// ---- helpers --------------------------------------------------------------

const INSTALL_ID = "aaaabbbb-0000-0000-0000-000000000001";

function makeStagingStatus(): InstallStatusResponse {
  return {
    installId: INSTALL_ID,
    status: "staging",
    packages: [
      { packageName: "my-pkg", state: "staging", errorMessage: null },
    ],
    startedAt: "2026-05-01T10:00:00Z",
    completedAt: null,
    error: null,
  };
}

function makeActiveStatus(): InstallStatusResponse {
  return {
    installId: INSTALL_ID,
    status: "active",
    packages: [
      { packageName: "my-pkg", state: "active", errorMessage: null },
    ],
    startedAt: "2026-05-01T10:00:00Z",
    completedAt: "2026-05-01T10:00:05Z",
    error: null,
  };
}

function makeFailedStatus(errorMsg = "Actor placement timeout"): InstallStatusResponse {
  return {
    installId: INSTALL_ID,
    status: "failed",
    packages: [
      {
        packageName: "my-pkg",
        state: "failed",
        errorMessage: errorMsg,
      },
    ],
    startedAt: "2026-05-01T10:00:00Z",
    completedAt: null,
    error: null,
  };
}

function renderPage(id = INSTALL_ID) {
  const client = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0, staleTime: 0 },
    },
  });
  const Wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={client}>{children}</QueryClientProvider>
  );
  return render(<InstallStatusClient id={id} />, { wrapper: Wrapper });
}

// ---- tests ----------------------------------------------------------------

describe("InstallStatusClient", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockPush.mockReset();
  });

  // ---- staging state ----

  it("renders staging state with spinner", async () => {
    getInstallStatus.mockResolvedValue(makeStagingStatus());
    renderPage();
    await waitFor(() => {
      expect(screen.getByTestId("install-status-staging")).toBeInTheDocument();
    });
    expect(screen.getByText(/installing/i)).toBeInTheDocument();
  });

  it("shows per-package detail in staging state", async () => {
    getInstallStatus.mockResolvedValue(makeStagingStatus());
    renderPage();
    await waitFor(() => {
      expect(screen.getByTestId("package-detail-row-my-pkg")).toBeInTheDocument();
    });
    expect(screen.getByTestId("package-state-my-pkg")).toHaveTextContent("staging");
  });

  it("does not show the Abort button in staging state", async () => {
    getInstallStatus.mockResolvedValue(makeStagingStatus());
    renderPage();
    await waitFor(() => screen.getByTestId("install-status-staging"));
    expect(screen.queryByTestId("abort-button")).not.toBeInTheDocument();
  });

  // ---- active state ----

  it("renders active state with success heading", async () => {
    getInstallStatus.mockResolvedValue(makeActiveStatus());
    renderPage();
    await waitFor(() => {
      expect(screen.getByTestId("install-status-active")).toBeInTheDocument();
    });
    expect(screen.getByText(/install complete/i)).toBeInTheDocument();
  });

  it("shows link to installed package in active state", async () => {
    getInstallStatus.mockResolvedValue(makeActiveStatus());
    renderPage();
    await waitFor(() => screen.getByTestId("install-status-active"));
    expect(screen.getByRole("link", { name: /view my-pkg/i })).toBeInTheDocument();
  });

  it("active state shows per-package active badge", async () => {
    getInstallStatus.mockResolvedValue(makeActiveStatus());
    renderPage();
    await waitFor(() => screen.getByTestId("install-status-active"));
    expect(screen.getByTestId("package-state-my-pkg")).toHaveTextContent("active");
  });

  // ---- failed state ----

  it("renders failed state with error heading", async () => {
    getInstallStatus.mockResolvedValue(makeFailedStatus());
    renderPage();
    await waitFor(() => {
      expect(screen.getByTestId("install-status-failed")).toBeInTheDocument();
    });
    expect(screen.getByText(/install failed/i)).toBeInTheDocument();
  });

  // ---- #2312: aggregate error-details card ----

  it("renders aggregate error-details card when any package has an errorMessage", async () => {
    getInstallStatus.mockResolvedValue(makeFailedStatus("Actor placement timeout"));
    renderPage();
    await waitFor(() => screen.getByTestId("install-status-failed"));
    expect(screen.getByTestId("install-error-details")).toBeInTheDocument();
    expect(screen.getByTestId("install-error-details")).toHaveTextContent(
      "my-pkg: Actor placement timeout",
    );
  });

  it("aggregates errorMessage values across multiple packages and prefixes each line with the package name", async () => {
    getInstallStatus.mockResolvedValue({
      installId: INSTALL_ID,
      status: "failed",
      packages: [
        { packageName: "pkg-a", state: "failed", errorMessage: "first failure" },
        { packageName: "pkg-b", state: "failed", errorMessage: "second\nthird" },
      ],
      startedAt: "2026-05-01T10:00:00Z",
      completedAt: null,
      error: null,
    });
    renderPage();
    await waitFor(() => screen.getByTestId("install-status-failed"));
    const card = screen.getByTestId("install-error-details");
    expect(card).toHaveTextContent("pkg-a: first failure");
    expect(card).toHaveTextContent("pkg-b: second");
    expect(card).toHaveTextContent("pkg-b: third");
  });

  it("does not render aggregate error-details card in staging state", async () => {
    getInstallStatus.mockResolvedValue(makeStagingStatus());
    renderPage();
    await waitFor(() => screen.getByTestId("install-status-staging"));
    expect(screen.queryByTestId("install-error-details")).not.toBeInTheDocument();
  });

  it("does not render aggregate error-details card in active state", async () => {
    getInstallStatus.mockResolvedValue(makeActiveStatus());
    renderPage();
    await waitFor(() => screen.getByTestId("install-status-active"));
    expect(screen.queryByTestId("install-error-details")).not.toBeInTheDocument();
  });

  it("shows the Abort button in failed state", async () => {
    getInstallStatus.mockResolvedValue(makeFailedStatus());
    renderPage();
    await waitFor(() => screen.getByTestId("install-status-failed"));
    expect(screen.queryByTestId("retry-button")).not.toBeInTheDocument();
    expect(screen.getByTestId("abort-button")).toBeInTheDocument();
  });

  it("shows per-package error message in failed state", async () => {
    getInstallStatus.mockResolvedValue(makeFailedStatus("Dapr placement timeout"));
    renderPage();
    await waitFor(() => screen.getByTestId("install-status-failed"));
    // #2312 now also renders an aggregate "Error details" card with the
    // same message, so the per-package row's destructive paragraph
    // becomes one of two matches. Scope to the per-row to keep the
    // pre-#2312 contract intact.
    const row = screen.getByTestId("package-detail-row-my-pkg");
    expect(row).toHaveTextContent(/dapr placement timeout/i);
  });

  // ---- abort action ----

  it("Abort button calls abortInstall API and redirects", async () => {
    getInstallStatus.mockResolvedValue(makeFailedStatus());
    abortInstall.mockResolvedValue(undefined);
    renderPage();
    await waitFor(() => screen.getByTestId("abort-button"));
    await act(async () => {
      fireEvent.click(screen.getByTestId("abort-button"));
    });
    await waitFor(() => {
      expect(abortInstall).toHaveBeenCalledWith(INSTALL_ID);
      expect(mockPush).toHaveBeenCalledWith(
        "/settings/packages/my-pkg",
      );
    });
  });

  // ---- 404 not found ----

  it("renders not-found state when install id returns null", async () => {
    getInstallStatus.mockResolvedValue(null);
    renderPage("unknown-id");
    await waitFor(() => {
      expect(screen.getByTestId("install-not-found")).toBeInTheDocument();
    });
  });
});
