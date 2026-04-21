import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { UnitNode } from "../aggregate";

vi.mock("@/components/units/tab-impls/boundary-tab", () => ({
  BoundaryTab: ({ unitId }: { unitId: string }) => (
    <div data-testid="legacy-boundary" data-unit-id={unitId} />
  ),
}));
vi.mock("@/components/units/tab-impls/connector-tab", () => ({
  ConnectorTab: ({ unitId }: { unitId: string }) => (
    <div data-testid="legacy-connector" data-unit-id={unitId} />
  ),
}));
vi.mock("@/components/units/tab-impls/execution-tab", () => ({
  ExecutionTab: ({ unitId }: { unitId: string }) => (
    <div data-testid="legacy-execution" data-unit-id={unitId} />
  ),
}));
vi.mock("@/components/units/tab-impls/secrets-tab", () => ({
  SecretsTab: ({ unitId }: { unitId: string }) => (
    <div data-testid="legacy-secrets" data-unit-id={unitId} />
  ),
}));
vi.mock("@/components/units/tab-impls/skills-tab", () => ({
  SkillsTab: ({ unitId }: { unitId: string }) => (
    <div data-testid="legacy-skills" data-unit-id={unitId} />
  ),
}));

import UnitConfigTab from "./unit-config";

describe("UnitConfigTab", () => {
  it("renders boundary / execution / connector / skills / secrets sections", () => {
    const node: UnitNode = {
      kind: "Unit",
      id: "engineering",
      name: "Engineering",
      status: "running",
    };
    render(<UnitConfigTab node={node} path={[node]} />);

    expect(screen.getByTestId("tab-unit-config")).toBeInTheDocument();
    expect(screen.getByTestId("legacy-boundary").dataset.unitId).toBe(
      "engineering",
    );
    expect(screen.getByTestId("legacy-execution").dataset.unitId).toBe(
      "engineering",
    );
    expect(screen.getByTestId("legacy-connector").dataset.unitId).toBe(
      "engineering",
    );
    expect(screen.getByTestId("legacy-skills").dataset.unitId).toBe(
      "engineering",
    );
    expect(screen.getByTestId("legacy-secrets").dataset.unitId).toBe(
      "engineering",
    );
  });
});
