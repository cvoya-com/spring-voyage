import { fireEvent, render, screen, within } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { PackageSummary } from "@/lib/api/types";

const usePackagesMock = vi.hoisted(() => vi.fn());

vi.mock("@/lib/api/queries", () => ({
  usePackages: () => usePackagesMock(),
}));

import { SourcePackagePicker } from "./source-package-picker";

function makePackage(
  overrides: Partial<PackageSummary> = {},
): PackageSummary {
  return {
    name: overrides.name ?? "agent-pack",
    description: overrides.description ?? "Agent package",
    unitTemplateCount: overrides.unitTemplateCount ?? 0,
    agentTemplateCount: overrides.agentTemplateCount ?? 1,
    skillCount: overrides.skillCount ?? 0,
    connectorCount: overrides.connectorCount ?? 0,
    workflowCount: overrides.workflowCount ?? 0,
    version: overrides.version ?? null,
  } as PackageSummary;
}

function mockPackages(packages: PackageSummary[]) {
  usePackagesMock.mockReturnValue({
    data: packages,
    isPending: false,
    isError: false,
    error: null,
  });
}

function renderPicker(onSelect = vi.fn()) {
  return {
    onSelect,
    ...render(
      <SourcePackagePicker
        onSelect={onSelect}
        onBack={vi.fn()}
        onCancel={vi.fn()}
      />,
    ),
  };
}

describe("SourcePackagePicker", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockPackages([]);
  });

  it("only shows packages with agent templates", () => {
    mockPackages([
      makePackage({
        name: "agent-pack",
        description: "Contains agent templates.",
        agentTemplateCount: 2,
      }),
      makePackage({
        name: "unit-only",
        description: "Contains only units.",
        unitTemplateCount: 1,
        agentTemplateCount: 0,
      }),
    ]);

    renderPicker();

    const list = screen.getByTestId("package-picker-list");
    expect(
      within(list).getByTestId("package-picker-item-agent-pack"),
    ).toBeInTheDocument();
    expect(
      screen.queryByTestId("package-picker-item-unit-only"),
    ).not.toBeInTheDocument();
  });

  it("filters packages by search text", () => {
    mockPackages([
      makePackage({ name: "engineering-agents", agentTemplateCount: 2 }),
      makePackage({ name: "research-agents", agentTemplateCount: 1 }),
    ]);

    renderPicker();

    fireEvent.change(screen.getByTestId("package-picker-search"), {
      target: { value: "research" },
    });

    expect(
      screen.queryByTestId("package-picker-item-engineering-agents"),
    ).not.toBeInTheDocument();
    expect(
      screen.getByTestId("package-picker-item-research-agents"),
    ).toBeInTheDocument();
  });

  it("disables confirm until a package is selected and then calls onSelect", () => {
    const onSelect = vi.fn();
    mockPackages([
      makePackage({ name: "agent-pack", agentTemplateCount: 2 }),
    ]);

    renderPicker(onSelect);

    const confirm = screen.getByTestId("package-picker-confirm");
    expect(confirm).toBeDisabled();

    fireEvent.click(screen.getByTestId("package-picker-item-agent-pack"));

    expect(confirm).toBeEnabled();
    fireEvent.click(confirm);

    expect(onSelect).toHaveBeenCalledWith("agent-pack");
  });
});
