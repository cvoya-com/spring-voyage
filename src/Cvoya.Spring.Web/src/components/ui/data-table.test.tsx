// Tests for the virtualised DataTable primitive (#911).
//
// The virtualiser relies on DOM scroll measurements that jsdom doesn't
// emulate faithfully — `useVirtualizer` won't render any virtual rows in
// a jsdom context unless the scroll container reports a non-zero height.
// We patch `getBoundingClientRect` on the container element so the
// virtualiser sees a real viewport and renders the first few items.

import { render, screen, fireEvent } from "@testing-library/react";
import { describe, expect, it, beforeAll } from "vitest";

import { DataTable, type DataTableColumn } from "./data-table";

// Make the scroll container report a real height so the virtualiser
// renders rows.
beforeAll(() => {
  Object.defineProperty(HTMLElement.prototype, "getBoundingClientRect", {
    configurable: true,
    value: () => ({
      width: 600,
      height: 320,
      top: 0,
      left: 0,
      right: 600,
      bottom: 320,
      x: 0,
      y: 0,
      toJSON: () => ({}),
    }),
  });
  // scrollHeight must exceed clientHeight for the virtualiser to allocate rows.
  Object.defineProperty(HTMLElement.prototype, "scrollHeight", {
    configurable: true,
    get() { return 2000; },
  });
  Object.defineProperty(HTMLElement.prototype, "clientHeight", {
    configurable: true,
    get() { return 320; },
  });
});

interface TestRow {
  id: string;
  name: string;
  value: number;
}

const COLUMNS: DataTableColumn[] = [
  { key: "name", header: "Name" },
  { key: "value", header: "Value", className: "text-right" },
];

function renderCell(row: TestRow, col: DataTableColumn): React.ReactNode {
  if (col.key === "name") return <span>{row.name}</span>;
  if (col.key === "value") return <span>{row.value}</span>;
  return null;
}

function makeRows(count: number): TestRow[] {
  return Array.from({ length: count }, (_, i) => ({
    id: `row-${i}`,
    name: `Item ${i}`,
    value: i * 10,
  }));
}

describe("DataTable", () => {
  it("renders column headers", () => {
    render(
      <DataTable<TestRow>
        aria-label="Test table"
        columns={COLUMNS}
        rows={makeRows(5)}
        renderCell={renderCell}
        getRowKey={(r) => r.id}
      />,
    );

    expect(screen.getByText("Name")).toBeInTheDocument();
    expect(screen.getByText("Value")).toBeInTheDocument();
  });

  it("carries role=grid with the supplied aria-label", () => {
    render(
      <DataTable<TestRow>
        aria-label="Budget table"
        columns={COLUMNS}
        rows={makeRows(3)}
        renderCell={renderCell}
        getRowKey={(r) => r.id}
      />,
    );

    const grid = screen.getByRole("grid", { name: "Budget table" });
    expect(grid).toBeInTheDocument();
  });

  it("renders column headers with role=columnheader", () => {
    render(
      <DataTable<TestRow>
        aria-label="Test"
        columns={COLUMNS}
        rows={makeRows(2)}
        renderCell={renderCell}
        getRowKey={(r) => r.id}
      />,
    );

    const headers = screen.getAllByRole("columnheader");
    expect(headers.length).toBe(2);
    expect(headers[0]).toHaveTextContent("Name");
    expect(headers[1]).toHaveTextContent("Value");
  });

  it("renders empty rows array without crashing", () => {
    render(
      <DataTable<TestRow>
        aria-label="Empty table"
        columns={COLUMNS}
        rows={[]}
        renderCell={renderCell}
        getRowKey={(r) => r.id}
      />,
    );

    // No data rows, but headers still render.
    expect(screen.getByText("Name")).toBeInTheDocument();
    expect(screen.queryByRole("row", { name: /item/i })).not.toBeInTheDocument();
  });

  it("sets aria-rowcount to rows.length + 1 (header row included)", () => {
    const rows = makeRows(10);
    render(
      <DataTable<TestRow>
        aria-label="Test"
        columns={COLUMNS}
        rows={rows}
        renderCell={renderCell}
        getRowKey={(r) => r.id}
      />,
    );

    const grid = screen.getByRole("grid");
    expect(grid).toHaveAttribute("aria-rowcount", "11");
  });

  it("renders the header row with aria-rowindex=1", () => {
    render(
      <DataTable<TestRow>
        aria-label="Test"
        columns={COLUMNS}
        rows={makeRows(20)}
        estimateSize={() => 48}
        height={320}
        renderCell={renderCell}
        getRowKey={(r) => r.id}
      />,
    );

    // The sticky header row always renders regardless of virtualiser state.
    const rows = screen.getAllByRole("row");
    // First row is the sticky header.
    expect(rows[0]).toHaveAttribute("aria-rowindex", "1");
  });

  it("handles keyboard ArrowDown without throwing", () => {
    render(
      <DataTable<TestRow>
        aria-label="Test"
        columns={COLUMNS}
        rows={makeRows(10)}
        renderCell={renderCell}
        getRowKey={(r) => r.id}
      />,
    );

    const grid = screen.getByRole("grid");
    // Fire a keydown event with ArrowDown — should not throw.
    fireEvent.keyDown(grid, { key: "ArrowDown" });
    expect(grid).toBeInTheDocument();
  });
});
