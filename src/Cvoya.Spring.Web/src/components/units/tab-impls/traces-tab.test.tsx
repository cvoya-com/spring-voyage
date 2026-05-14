import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import { TracesTab } from "./traces-tab";

describe("TracesTab", () => {
  it("renders the V21-traces-api mock note and 6 fixture rows for Agent", () => {
    render(<TracesTab kind="Agent" id="ada" />);
    expect(screen.getByTestId("tab-agent-traces-mock-note")).toHaveTextContent(
      "V21-traces-api",
    );
    const rows = screen.getAllByRole("row");
    // 1 header + 6 body rows = 7.
    expect(rows.length).toBe(7);
  });

  it("renders the V21-traces-api mock note and 6 fixture rows for Unit", () => {
    render(<TracesTab kind="Unit" id="engineering" />);
    expect(screen.getByTestId("tab-unit-traces-mock-note")).toHaveTextContent(
      "V21-traces-api",
    );
    const rows = screen.getAllByRole("row");
    expect(rows.length).toBe(7);
  });

  it("seeds different fixtures for different ids (deterministic per id)", () => {
    const { container: a } = render(<TracesTab kind="Unit" id="engineering" />);
    const { container: b } = render(<TracesTab kind="Unit" id="marketing" />);
    // The first trace id is derived from the subject id's prefix + seed.
    expect(a.querySelector("td.font-mono")?.textContent).not.toBe(
      b.querySelector("td.font-mono")?.textContent,
    );
  });
});
