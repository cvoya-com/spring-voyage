import { describe, expect, it } from "vitest";

import {
  DEFAULT_NEIGHBOURS,
  DEFAULT_VIEW,
  EMPTY_URL_STATE,
  readUrlState,
  resolveWindow,
  toSnapshotFilters,
  writeUrlState,
  type InteractionsUrlState,
} from "./url-state";

describe("interactions url-state", () => {
  describe("round-trips every URL slot", () => {
    const populated: InteractionsUrlState = {
      unit: "unit-eng",
      participant: "agent://abc",
      since: "2026-05-27T10:00:00.000Z",
      until: "2026-05-27T11:00:00.000Z",
      neighbours: 1,
      bucket: "day",
      view: "matrix",
      live: true,
    };

    it("writes every populated slot to URL", () => {
      const qs = writeUrlState(populated);
      const params = new URLSearchParams(qs);
      expect(params.get("unit")).toBe("unit-eng");
      expect(params.get("participant")).toBe("agent://abc");
      expect(params.get("since")).toBe("2026-05-27T10:00:00.000Z");
      expect(params.get("until")).toBe("2026-05-27T11:00:00.000Z");
      expect(params.get("neighbours")).toBe("1");
      expect(params.get("bucket")).toBe("day");
      expect(params.get("view")).toBe("matrix");
      expect(params.get("live")).toBe("true");
    });

    it("reads every URL slot back into state", () => {
      const qs = writeUrlState(populated);
      const round = readUrlState(new URLSearchParams(qs));
      expect(round).toEqual(populated);
    });

    it("omits defaults to keep URLs short", () => {
      const qs = writeUrlState(EMPTY_URL_STATE);
      expect(qs).toBe("");
    });

    it("falls back to defaults when URL slots are missing", () => {
      const round = readUrlState(new URLSearchParams(""));
      expect(round.neighbours).toBe(DEFAULT_NEIGHBOURS);
      expect(round.view).toBe(DEFAULT_VIEW);
      expect(round.bucket).toBe("hour");
      expect(round.live).toBe(false);
    });

    it("clamps unknown neighbours / bucket / view values to defaults", () => {
      const round = readUrlState(
        new URLSearchParams("neighbours=99&bucket=year&view=bogus"),
      );
      expect(round.neighbours).toBe(DEFAULT_NEIGHBOURS);
      expect(round.bucket).toBe("hour");
      expect(round.view).toBe(DEFAULT_VIEW);
    });
  });

  describe("resolveWindow", () => {
    it("materialises a 10-minute window when both bounds are empty", () => {
      const state = { ...EMPTY_URL_STATE };
      const { since, until } = resolveWindow(state);
      const sinceMs = new Date(since).getTime();
      const untilMs = new Date(until).getTime();
      expect(untilMs - sinceMs).toBeCloseTo(10 * 60 * 1000, -3);
    });

    it("honours an explicit since while defaulting until", () => {
      const state: InteractionsUrlState = {
        ...EMPTY_URL_STATE,
        since: "2026-05-27T09:00:00.000Z",
      };
      const { since, until } = resolveWindow(state);
      expect(since).toBe("2026-05-27T09:00:00.000Z");
      expect(new Date(until).getTime()).toBeGreaterThan(
        new Date(since).getTime(),
      );
    });
  });

  describe("toSnapshotFilters", () => {
    it("propagates neighbours / unit / bucket", () => {
      const state: InteractionsUrlState = {
        ...EMPTY_URL_STATE,
        unit: "unit-x",
        neighbours: 1,
        bucket: "day",
      };
      const filters = toSnapshotFilters(state);
      expect(filters.unit).toBe("unit-x");
      expect(filters.neighbours).toBe(1);
      expect(filters.bucket).toBe("day");
      expect(filters.since).toBeTruthy();
      expect(filters.until).toBeTruthy();
    });

    it("drops empty optional fields", () => {
      const filters = toSnapshotFilters(EMPTY_URL_STATE);
      expect(filters.unit).toBeUndefined();
      expect(filters.participant).toBeUndefined();
    });
  });
});
