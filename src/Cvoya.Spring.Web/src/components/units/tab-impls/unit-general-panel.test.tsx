import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";

// Mock the embedded expertise panel so this test stays focused on the
// metadata edit + save plumbing.
vi.mock("@/components/expertise/unit-expertise-panel", () => ({
  UnitExpertisePanel: ({ unitId }: { unitId: string }) => (
    <div data-testid="legacy-unit-expertise" data-unit-id={unitId}>
      Unit expertise
    </div>
  ),
}));

vi.mock("@/components/ui/toast", () => ({
  useToast: () => ({ toast: vi.fn() }),
}));

const useUnitMock = vi.fn();
vi.mock("@/lib/api/queries", () => ({
  useUnit: (id: string) => useUnitMock(id),
}));

const updateUnitMock = vi.fn();
vi.mock("@/lib/api/client", () => ({
  api: {
    updateUnit: (...args: unknown[]) => updateUnitMock(...args),
  },
}));

import { UnitGeneralPanel } from "./unit-general-panel";

function withClient(ui: ReactNode): ReactNode {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  return <QueryClientProvider client={client}>{ui}</QueryClientProvider>;
}

describe("UnitGeneralPanel (#2331)", () => {
  beforeEach(() => {
    useUnitMock.mockReset();
    updateUnitMock.mockReset();
  });

  it("seeds the form from the persisted unit metadata", () => {
    useUnitMock.mockReturnValue({
      isPending: false,
      data: {
        displayName: "Engineering",
        description: "Builds stuff",
        model: "claude-3-5-sonnet-latest",
        color: "#5b8def",
      },
    });
    render(withClient(<UnitGeneralPanel unitId="engineering" />));

    expect(
      (screen.getByTestId("unit-general-display-name") as HTMLInputElement)
        .value,
    ).toBe("Engineering");
    expect(
      (screen.getByTestId("unit-general-description") as HTMLTextAreaElement)
        .value,
    ).toBe("Builds stuff");
    expect(
      (screen.getByTestId("unit-general-model") as HTMLInputElement).value,
    ).toBe("claude-3-5-sonnet-latest");
    expect(
      (screen.getByTestId("unit-general-color-hex") as HTMLInputElement).value,
    ).toBe("#5b8def");
  });

  it("renders the expertise editor inline under the metadata card", () => {
    useUnitMock.mockReturnValue({
      isPending: false,
      data: {
        displayName: "Engineering",
        description: "",
        model: "",
        color: "",
      },
    });
    render(withClient(<UnitGeneralPanel unitId="engineering" />));

    const expertise = screen.getByTestId("legacy-unit-expertise");
    expect(expertise.dataset.unitId).toBe("engineering");
  });

  it("disables Save until the form is dirty, then sends only the changed fields", async () => {
    useUnitMock.mockReturnValue({
      isPending: false,
      data: {
        displayName: "Engineering",
        description: "Builds stuff",
        model: "",
        color: "",
      },
    });
    updateUnitMock.mockResolvedValue(undefined);

    render(withClient(<UnitGeneralPanel unitId="engineering" />));

    const save = screen.getByTestId("unit-general-save") as HTMLButtonElement;
    expect(save.disabled).toBe(true);

    fireEvent.change(screen.getByTestId("unit-general-display-name"), {
      target: { value: "Eng Team" },
    });

    expect(save.disabled).toBe(false);
    fireEvent.click(save);

    await waitFor(() => {
      expect(updateUnitMock).toHaveBeenCalledWith("engineering", {
        displayName: "Eng Team",
      });
    });
  });

  it("shows a Revert button while dirty and resets the draft when clicked", () => {
    useUnitMock.mockReturnValue({
      isPending: false,
      data: {
        displayName: "Engineering",
        description: "",
        model: "",
        color: "",
      },
    });
    render(withClient(<UnitGeneralPanel unitId="engineering" />));

    fireEvent.change(screen.getByTestId("unit-general-display-name"), {
      target: { value: "Eng Team" },
    });
    expect(screen.getByTestId("unit-general-revert")).toBeInTheDocument();
    fireEvent.click(screen.getByTestId("unit-general-revert"));

    expect(
      (screen.getByTestId("unit-general-display-name") as HTMLInputElement)
        .value,
    ).toBe("Engineering");
  });

  it("renders the loading skeleton while the unit detail query is pending", () => {
    useUnitMock.mockReturnValue({ isPending: true, data: undefined });
    render(withClient(<UnitGeneralPanel unitId="engineering" />));
    expect(screen.getByTestId("unit-general-skeleton")).toBeInTheDocument();
  });
});
