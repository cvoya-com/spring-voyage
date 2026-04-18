import type { ReactNode } from "react";

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import { BoundaryTab } from "./boundary-tab";

const mockGetUnitBoundary = vi.fn();
const mockSetUnitBoundary = vi.fn();
const mockClearUnitBoundary = vi.fn();
const mockToast = vi.fn();

vi.mock("@/lib/api/client", () => ({
  api: {
    getUnitBoundary: (...args: unknown[]) => mockGetUnitBoundary(...args),
    setUnitBoundary: (...args: unknown[]) => mockSetUnitBoundary(...args),
    clearUnitBoundary: (...args: unknown[]) => mockClearUnitBoundary(...args),
  },
}));

vi.mock("@/components/ui/toast", () => ({
  useToast: () => ({ toast: mockToast }),
}));

function Wrapper({ children }: { children: ReactNode }) {
  const client = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0, staleTime: 0 },
    },
  });
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

describe("BoundaryTab", () => {
  beforeEach(() => {
    mockGetUnitBoundary.mockReset();
    mockSetUnitBoundary.mockReset();
    mockClearUnitBoundary.mockReset();
    mockToast.mockReset();
  });

  it("renders empty (transparent) state when the boundary has no rules", async () => {
    mockGetUnitBoundary.mockResolvedValue({
      opacities: null,
      projections: null,
      syntheses: null,
    });

    render(
      <Wrapper>
        <BoundaryTab unitId="eng-team" />
      </Wrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("Transparent")).toBeInTheDocument();
    });
    expect(screen.getByText("No opacity rules.")).toBeInTheDocument();
    expect(screen.getByText("No projection rules.")).toBeInTheDocument();
    expect(screen.getByText("No synthesis rules.")).toBeInTheDocument();
  });

  it("renders existing rules from the server", async () => {
    mockGetUnitBoundary.mockResolvedValue({
      opacities: [
        { domainPattern: "secret-*", originPattern: "agent://internal-*" },
      ],
      projections: [
        {
          domainPattern: "react",
          originPattern: null,
          renameTo: "frontend",
          retag: null,
          overrideLevel: null,
        },
      ],
      syntheses: [
        {
          name: "team-frontend",
          domainPattern: "react",
          originPattern: null,
          description: null,
          level: "expert",
        },
      ],
    });

    render(
      <Wrapper>
        <BoundaryTab unitId="eng-team" />
      </Wrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("Configured")).toBeInTheDocument();
    });
    expect(screen.getByTestId("opacity-rules")).toBeInTheDocument();
    expect(screen.getByTestId("projection-rules")).toBeInTheDocument();
    expect(screen.getByTestId("synthesis-rules")).toBeInTheDocument();
  });

  it("PUTs the full boundary when the user adds an opacity rule and saves", async () => {
    mockGetUnitBoundary.mockResolvedValue({
      opacities: null,
      projections: null,
      syntheses: null,
    });
    mockSetUnitBoundary.mockResolvedValue({
      opacities: [{ domainPattern: "secret-*", originPattern: null }],
      projections: null,
      syntheses: null,
    });

    render(
      <Wrapper>
        <BoundaryTab unitId="eng-team" />
      </Wrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("Transparent")).toBeInTheDocument();
    });

    fireEvent.change(screen.getAllByPlaceholderText("secret-*")[0], {
      target: { value: "secret-*" },
    });
    fireEvent.click(screen.getByRole("button", { name: /add opacity/i }));
    fireEvent.click(screen.getByRole("button", { name: /save boundary/i }));

    await waitFor(() => {
      expect(mockSetUnitBoundary).toHaveBeenCalledWith("eng-team", {
        opacities: [{ domainPattern: "secret-*", originPattern: null }],
        projections: null,
        syntheses: null,
      });
    });
  });

  it("DELETEs the boundary when the user confirms Clear", async () => {
    mockGetUnitBoundary.mockResolvedValue({
      opacities: [{ domainPattern: "foo", originPattern: null }],
      projections: null,
      syntheses: null,
    });
    mockClearUnitBoundary.mockResolvedValue(undefined);
    // Second getUnitBoundary call after invalidation.
    mockGetUnitBoundary.mockResolvedValueOnce({
      opacities: [{ domainPattern: "foo", originPattern: null }],
      projections: null,
      syntheses: null,
    });

    render(
      <Wrapper>
        <BoundaryTab unitId="eng-team" />
      </Wrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("Configured")).toBeInTheDocument();
    });

    fireEvent.click(screen.getByRole("button", { name: /clear all rules/i }));
    // ConfirmDialog mounts; click Clear (the destructive confirm).
    const confirmButton = await screen.findByRole("button", { name: "Clear" });
    fireEvent.click(confirmButton);

    await waitFor(() => {
      expect(mockClearUnitBoundary).toHaveBeenCalledWith("eng-team");
    });
  });
});
