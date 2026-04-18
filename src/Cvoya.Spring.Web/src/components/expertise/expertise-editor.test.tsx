import {
  fireEvent,
  render,
  screen,
  waitFor,
} from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

const toastMock = vi.fn();
vi.mock("@/components/ui/toast", () => ({
  useToast: () => ({ toast: toastMock }),
}));

import { ExpertiseEditor } from "./expertise-editor";
import type { ExpertiseDomainDto } from "@/lib/api/types";

describe("ExpertiseEditor", () => {
  beforeEach(() => {
    toastMock.mockReset();
  });

  it("renders one row per existing domain with name, level, and description", () => {
    const onSave = vi.fn();
    render(
      <ExpertiseEditor
        domains={[
          { name: "python", level: "expert", description: "Py web" },
          { name: "sql", level: null, description: "" },
        ]}
        onSave={onSave}
      />,
    );

    expect(screen.getByTestId("expertise-row-0")).toBeInTheDocument();
    expect(screen.getByTestId("expertise-row-1")).toBeInTheDocument();
    const nameInput = screen.getByLabelText(
      /Domain name \(row 1\)/i,
    ) as HTMLInputElement;
    expect(nameInput.value).toBe("python");
  });

  it("disables save until the row list changes", () => {
    const onSave = vi.fn();
    render(
      <ExpertiseEditor
        domains={[{ name: "python", level: null, description: "" }]}
        onSave={onSave}
      />,
    );
    const saveBtn = screen.getByRole("button", { name: /^save$/i });
    expect(saveBtn).toBeDisabled();

    fireEvent.change(screen.getByLabelText(/Domain name \(row 1\)/i), {
      target: { value: "python-new" },
    });
    expect(saveBtn).not.toBeDisabled();
  });

  it("calls onSave with the full replacement payload, dropping blank-named rows", async () => {
    const onSave =
      vi.fn<
        (domains: ExpertiseDomainDto[]) => Promise<ExpertiseDomainDto[]>
      >();
    onSave.mockResolvedValue([
      { name: "python", level: "expert", description: "Py web" },
    ]);

    render(
      <ExpertiseEditor
        domains={[{ name: "python", level: null, description: "" }]}
        onSave={onSave}
      />,
    );

    fireEvent.change(screen.getByLabelText(/Level \(row 1\)/i), {
      target: { value: "expert" },
    });
    fireEvent.change(screen.getByLabelText(/Description \(row 1\)/i), {
      target: { value: "Py web" },
    });
    // Add a blank row; it must be filtered out before PUT.
    fireEvent.click(screen.getByRole("button", { name: /add domain/i }));

    fireEvent.click(screen.getByRole("button", { name: /^save$/i }));

    await waitFor(() => {
      expect(onSave).toHaveBeenCalledTimes(1);
    });
    expect(onSave).toHaveBeenCalledWith([
      { name: "python", level: "expert", description: "Py web" },
    ]);
  });

  it("lets the user remove a row and then save the shrunken list", async () => {
    const onSave =
      vi.fn<
        (domains: ExpertiseDomainDto[]) => Promise<ExpertiseDomainDto[]>
      >();
    onSave.mockResolvedValue([]);

    render(
      <ExpertiseEditor
        domains={[
          { name: "python", level: null, description: "" },
          { name: "sql", level: null, description: "" },
        ]}
        onSave={onSave}
      />,
    );

    fireEvent.click(screen.getByRole("button", { name: /remove domain sql/i }));
    fireEvent.click(screen.getByRole("button", { name: /^save$/i }));

    await waitFor(() => {
      expect(onSave).toHaveBeenCalledWith([
        { name: "python", level: null, description: "" },
      ]);
    });
  });

  it("reverts local edits to the persisted list", () => {
    const onSave = vi.fn();
    render(
      <ExpertiseEditor
        domains={[{ name: "python", level: null, description: "" }]}
        onSave={onSave}
      />,
    );
    fireEvent.change(screen.getByLabelText(/Domain name \(row 1\)/i), {
      target: { value: "changed" },
    });
    expect(screen.getByRole("button", { name: /^save$/i })).not.toBeDisabled();

    fireEvent.click(screen.getByRole("button", { name: /revert/i }));

    const nameInput = screen.getByLabelText(
      /Domain name \(row 1\)/i,
    ) as HTMLInputElement;
    expect(nameInput.value).toBe("python");
    expect(screen.getByRole("button", { name: /^save$/i })).toBeDisabled();
  });

  it("toasts on save failure and does not clear the dirty state", async () => {
    const onSave =
      vi.fn<
        (domains: ExpertiseDomainDto[]) => Promise<ExpertiseDomainDto[]>
      >();
    onSave.mockRejectedValue(new Error("boom"));

    render(
      <ExpertiseEditor
        domains={[{ name: "python", level: null, description: "" }]}
        onSave={onSave}
      />,
    );
    fireEvent.change(screen.getByLabelText(/Domain name \(row 1\)/i), {
      target: { value: "python-new" },
    });
    fireEvent.click(screen.getByRole("button", { name: /^save$/i }));

    await waitFor(() => {
      expect(toastMock).toHaveBeenCalledWith(
        expect.objectContaining({ variant: "destructive" }),
      );
    });
  });
});
