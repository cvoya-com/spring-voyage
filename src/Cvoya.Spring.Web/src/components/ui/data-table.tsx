"use client";

// Virtualised data-table primitive (#911). Uses @tanstack/react-virtual so
// large analytics lists (up to the §3 tenant-tree budget of 500 rows) stay
// snappy; the DOM only renders the visible slice.
//
// Design constraints (DESIGN.md § 12.1 / § 12):
//   - Wraps the base <Table> chrome so the visual treatment is identical to
//     non-virtualised tables: rows are `border-b border-border
//     transition-colors hover:bg-muted/50`.
//   - Sticky header so column labels don't scroll away.
//   - Keyboard navigation: arrow keys move the focused row; Home / End jump
//     to first / last. Tab moves focus out of the list rather than
//     row-by-row (keeps the document tab order sane).
//   - Screen-reader announcements: the outer element is `role="grid"` with
//     `aria-label`, and each row carries `aria-rowindex` (1-based, offset by
//     the header). Individual cells carry `role="gridcell"` / `role="columnheader"`
//     so AT can navigate by row and column.
//
// Usage:
//   <DataTable
//     aria-label="Per-agent budgets"
//     columns={[
//       { key: "name", header: "Agent", className: "min-w-0" },
//       { key: "budget", header: "Budget", className: "w-32 text-right" },
//     ]}
//     rows={agentRows}
//     renderCell={(row, col) => ...}
//     estimateSize={() => 48}   // px; defaults to 48
//     height={320}              // px; defaults to 320
//   />

import { useRef, useCallback, useMemo, type KeyboardEvent } from "react";
import { useVirtualizer } from "@tanstack/react-virtual";
import { cn } from "@/lib/utils";

export interface DataTableColumn {
  /** Stable key used for `key` prop on cells / headers. */
  key: string;
  /** Text rendered in the sticky header. */
  header: string;
  /** Extra Tailwind classes applied to both `<th>` and every `<td>` in this column. */
  className?: string;
}

export interface DataTableProps<R> {
  /** Accessible label for the whole table (announced by screen readers). */
  "aria-label": string;
  /** Column descriptors — drives the sticky header row. */
  columns: DataTableColumn[];
  /** Full dataset; virtualisation slices this in the render path. */
  rows: R[];
  /**
   * Render the cell content for `row[col.key]`. Called only for visible
   * rows, so avoid side-effects.
   */
  renderCell: (row: R, col: DataTableColumn, rowIndex: number) => React.ReactNode;
  /**
   * Returns the estimated row height in px. Defaults to 48 px (comfortable
   * touch target). If your rows vary substantially, supply a measurement
   * function that inspects the row data.
   */
  estimateSize?: (index: number) => number;
  /**
   * Fixed height of the scrollable viewport in px. Defaults to 320.
   * Set to `"auto"` to let the container expand to fit its parent (use
   * only when the parent has a known, bounded height).
   */
  height?: number;
  /** Additional Tailwind classes on the outer scrollable container. */
  className?: string;
  /** Optional row key extractor; defaults to `String(rowIndex)`. */
  getRowKey?: (row: R, index: number) => string;
}

/**
 * Virtualised data-table. Renders only the visible slice of `rows` so
 * large lists (≥ 500 rows) don't incur full-DOM layout cost.
 *
 * a11y contract:
 *   - `role="grid"` with `aria-label` on the outer scroll container.
 *   - `role="row"` + `aria-rowindex` (1-based, includes header) on every
 *     rendered row.
 *   - `role="columnheader"` on header cells, `role="gridcell"` on data cells.
 *   - Arrow-key navigation between rows (`ArrowUp` / `ArrowDown`).
 *   - `Home` / `End` jump to first / last row.
 *   - Focus is tracked per `data-row-index`; the virtualiser keeps the
 *     focused row in the viewport before scrolling.
 */
export function DataTable<R>({
  "aria-label": ariaLabel,
  columns,
  rows,
  renderCell,
  estimateSize = () => 48,
  height = 320,
  className,
  getRowKey,
}: DataTableProps<R>) {
  const parentRef = useRef<HTMLDivElement>(null);

  // useVirtualizer returns functions that React Compiler cannot memoize — expected and
  // documented behaviour for @tanstack/react-virtual. The component's own state is stable.
  // eslint-disable-next-line react-hooks/incompatible-library
  const virtualizer = useVirtualizer({
    count: rows.length,
    getScrollElement: () => parentRef.current,
    estimateSize,
    overscan: 5,
  });

  const virtualItems = virtualizer.getVirtualItems();
  const totalSize = virtualizer.getTotalSize();

  // Track which data-row is focused so arrow-key navigation works.
  const focusedRowRef = useRef<number | null>(null);

  const focusRow = useCallback(
    (idx: number) => {
      const el = parentRef.current?.querySelector<HTMLElement>(
        `[data-row-index="${idx}"]`,
      );
      if (el) {
        el.focus();
        focusedRowRef.current = idx;
      } else {
        // Row not yet rendered — scroll it into view then retry.
        virtualizer.scrollToIndex(idx, { behavior: "smooth" });
        // A rAF gives the virtualiser one frame to render the new item.
        requestAnimationFrame(() => {
          const deferred = parentRef.current?.querySelector<HTMLElement>(
            `[data-row-index="${idx}"]`,
          );
          deferred?.focus();
          focusedRowRef.current = idx;
        });
      }
    },
    [virtualizer],
  );

  const handleKeyDown = useCallback(
    (e: KeyboardEvent<HTMLDivElement>) => {
      const current = focusedRowRef.current;
      if (current === null) return;
      if (e.key === "ArrowDown") {
        e.preventDefault();
        if (current < rows.length - 1) focusRow(current + 1);
      } else if (e.key === "ArrowUp") {
        e.preventDefault();
        if (current > 0) focusRow(current - 1);
      } else if (e.key === "Home") {
        e.preventDefault();
        focusRow(0);
      } else if (e.key === "End") {
        e.preventDefault();
        focusRow(rows.length - 1);
      }
    },
    [rows.length, focusRow],
  );

  const colCount = columns.length;

  // Header cell classes — same visual treatment as table.tsx TableHead.
  const headCellBase =
    "h-10 px-3 text-left align-middle font-medium text-muted-foreground text-xs";

  // Data row classes — same as table.tsx TableRow.
  const rowBase =
    "flex w-full items-center border-b border-border transition-colors hover:bg-muted/50 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-inset";

  const cellBase = "px-3 py-2 align-middle text-sm";

  // Pre-compute column flex strings so each row can lay out columns
  // identically to the sticky header without a real <table>.
  const colStyles = useMemo(
    () => columns.map((c) => c.className ?? "flex-1 min-w-0"),
    [columns],
  );

  return (
    <div
      ref={parentRef}
      role="grid"
      aria-label={ariaLabel}
      aria-rowcount={rows.length + 1}
      aria-colcount={colCount}
      tabIndex={0}
      onKeyDown={handleKeyDown}
      onFocus={(e) => {
        // When focus enters the grid from outside and no row is focused yet,
        // focus the first visible row.
        if (
          focusedRowRef.current === null &&
          e.target === parentRef.current &&
          virtualItems.length > 0
        ) {
          const firstIdx = virtualItems[0]?.index ?? 0;
          focusRow(firstIdx);
        }
      }}
      className={cn(
        "relative overflow-auto rounded-md border border-border",
        className,
      )}
      style={{ height }}
    >
      {/* Sticky header */}
      <div
        role="row"
        aria-rowindex={1}
        className="sticky top-0 z-10 flex w-full bg-card border-b border-border"
      >
        {columns.map((col, ci) => (
          <div
            key={col.key}
            role="columnheader"
            aria-colindex={ci + 1}
            className={cn(headCellBase, colStyles[ci])}
          >
            {col.header}
          </div>
        ))}
      </div>

      {/* Virtual canvas */}
      <div
        style={{ height: totalSize, position: "relative" }}
        aria-hidden="false"
      >
        {virtualItems.map((vRow) => {
          const row = rows[vRow.index];
          const rowKey = getRowKey ? getRowKey(row, vRow.index) : String(vRow.index);
          return (
            <div
              key={rowKey}
              role="row"
              aria-rowindex={vRow.index + 2} // +1 for header, +1 for 1-based
              data-row-index={vRow.index}
              tabIndex={-1}
              className={rowBase}
              style={{
                position: "absolute",
                top: 0,
                left: 0,
                right: 0,
                transform: `translateY(${vRow.start}px)`,
                height: `${vRow.size}px`,
              }}
              onFocus={() => {
                focusedRowRef.current = vRow.index;
              }}
            >
              {columns.map((col, ci) => (
                <div
                  key={col.key}
                  role="gridcell"
                  aria-colindex={ci + 1}
                  className={cn(cellBase, colStyles[ci])}
                >
                  {renderCell(row, col, vRow.index)}
                </div>
              ))}
            </div>
          );
        })}
      </div>
    </div>
  );
}
