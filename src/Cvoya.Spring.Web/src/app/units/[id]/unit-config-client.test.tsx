import {
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type { UnitResponse } from "@/lib/api/types";

// Mock the API module.
const getUnit = vi.fn<(id: string) => Promise<UnitResponse>>();
const getUnitCost = vi.fn();
const getUnitReadiness = vi.fn();
const deleteUnit = vi.fn<(id: string) => Promise<void>>();

vi.mock("@/lib/api/client", () => ({
  api: {
    getUnit: (id: string) => getUnit(id),
    getUnitCost: (id: string) => getUnitCost(id),
    getUnitReadiness: (id: string) => getUnitReadiness(id),
    deleteUnit: (id: string) => deleteUnit(id),
    // Stubs for other calls the component makes on mount.
    startUnit: vi.fn(),
    stopUnit: vi.fn(),
    updateUnit: vi.fn(),
  },
}));

const toastMock = vi.fn();
vi.mock("@/components/ui/toast", () => ({
  useToast: () => ({ toast: toastMock }),
}));

const pushMock = vi.fn();
vi.mock("next/navigation", () => ({
  useRouter: () => ({ push: pushMock }),
}));

vi.mock("next/link", () => ({
  default: ({
    href,
    children,
    ...rest
  }: {
    href: string;
    children: React.ReactNode;
  } & Record<string, unknown>) => (
    <a href={href} {...rest}>
      {children}
    </a>
  ),
}));

import UnitConfigClient from "./unit-config-client";

function makeUnit(overrides: Partial<UnitResponse> = {}): UnitResponse {
  return {
    id: "actor-id",
    name: "engineering",
    displayName: "Engineering",
    description: "The engineering unit",
    registeredAt: new Date().toISOString(),
    status: "Draft",
    model: null,
    color: null,
    ...overrides,
  } as UnitResponse;
}

describe("UnitConfigClient — delete unit", () => {
  beforeEach(() => {
    getUnit.mockReset();
    getUnitCost.mockReset();
    getUnitReadiness.mockReset();
    deleteUnit.mockReset();
    toastMock.mockReset();
    pushMock.mockReset();
    // Default: cost returns nothing interesting.
    getUnitCost.mockRejectedValue(new Error("no cost data"));
    // Default: unit is ready (model configured).
    getUnitReadiness.mockResolvedValue({ isReady: true, missingRequirements: [] });
  });

  afterEach(() => {
    vi.clearAllMocks();
  });

  it("renders the Delete button on the unit detail page", async () => {
    getUnit.mockResolvedValue(makeUnit());

    render(<UnitConfigClient id="engineering" />);

    await waitFor(() => {
      expect(
        screen.getByRole("button", { name: /delete/i }),
      ).toBeInTheDocument();
    });
  });

  it("opens the confirm dialog when Delete is clicked", async () => {
    getUnit.mockResolvedValue(makeUnit());

    render(<UnitConfigClient id="engineering" />);

    await waitFor(() => {
      expect(
        screen.getByRole("button", { name: /delete/i }),
      ).toBeInTheDocument();
    });

    fireEvent.click(screen.getByRole("button", { name: /delete/i }));

    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByText(/Delete unit/i)).toBeInTheDocument();
    // The description text appears both as a visible paragraph and as a
    // sr-only fallback inside the ConfirmDialog, so use getAllByText.
    expect(
      within(dialog).getAllByText(/Are you sure you want to delete/i).length,
    ).toBeGreaterThan(0);
  });

  it("closes the dialog on Cancel without calling the API", async () => {
    getUnit.mockResolvedValue(makeUnit());

    render(<UnitConfigClient id="engineering" />);

    await waitFor(() => {
      expect(
        screen.getByRole("button", { name: /delete/i }),
      ).toBeInTheDocument();
    });

    fireEvent.click(screen.getByRole("button", { name: /delete/i }));
    const dialog = await screen.findByRole("dialog");
    fireEvent.click(within(dialog).getByRole("button", { name: /cancel/i }));

    await waitFor(() => {
      expect(screen.queryByRole("dialog")).toBeNull();
    });
    expect(deleteUnit).not.toHaveBeenCalled();
  });

  it("calls DELETE and navigates to /units on confirm", async () => {
    getUnit.mockResolvedValue(makeUnit());
    deleteUnit.mockResolvedValue(undefined);

    render(<UnitConfigClient id="engineering" />);

    await waitFor(() => {
      expect(
        screen.getByRole("button", { name: /delete/i }),
      ).toBeInTheDocument();
    });

    fireEvent.click(screen.getByRole("button", { name: /delete/i }));
    const dialog = await screen.findByRole("dialog");
    fireEvent.click(
      within(dialog).getByRole("button", { name: /^delete$/i }),
    );

    await waitFor(() => {
      expect(deleteUnit).toHaveBeenCalledWith("engineering");
    });

    await waitFor(() => {
      expect(pushMock).toHaveBeenCalledWith("/units");
    });

    expect(toastMock).toHaveBeenCalledWith(
      expect.objectContaining({ title: "Unit deleted" }),
    );
  });

  it("shows an error toast and keeps the dialog open on API failure", async () => {
    getUnit.mockResolvedValue(makeUnit());
    deleteUnit.mockRejectedValue(
      new Error("API error 404: Not Found"),
    );

    render(<UnitConfigClient id="engineering" />);

    await waitFor(() => {
      expect(
        screen.getByRole("button", { name: /delete/i }),
      ).toBeInTheDocument();
    });

    fireEvent.click(screen.getByRole("button", { name: /delete/i }));
    const dialog = await screen.findByRole("dialog");
    fireEvent.click(
      within(dialog).getByRole("button", { name: /^delete$/i }),
    );

    await waitFor(() => {
      expect(toastMock).toHaveBeenCalledWith(
        expect.objectContaining({
          title: "Delete failed",
          variant: "destructive",
        }),
      );
    });

    // Dialog should still be open — the user may retry or cancel.
    expect(screen.getByRole("dialog")).toBeInTheDocument();
    expect(pushMock).not.toHaveBeenCalled();
  });
});
