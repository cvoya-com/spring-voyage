// Unit tests for the boundary diff helpers used by the YAML upload
// preview (#524).

import { describe, expect, it } from "vitest";

import { diffBoundaries } from "./diff";

describe("diffBoundaries", () => {
  it("flags an identical upload as a no-op", () => {
    const current = {
      opacities: [{ domainPattern: "foo", originPattern: null }],
      projections: null,
      syntheses: null,
    };
    const incoming = {
      opacities: [{ domainPattern: "foo", originPattern: null }],
      projections: null,
      syntheses: null,
    };
    const diff = diffBoundaries(current, incoming);
    expect(diff.isNoOp).toBe(true);
    expect(diff.addedCount).toBe(0);
    expect(diff.removedCount).toBe(0);
    expect(diff.opacities).toEqual([
      {
        status: "same",
        rule: { domainPattern: "foo", originPattern: null },
      },
    ]);
  });

  it("counts added and removed rules across dimensions", () => {
    const current = {
      opacities: [{ domainPattern: "keep", originPattern: null }],
      projections: [
        {
          domainPattern: "drop",
          originPattern: null,
          renameTo: null,
          retag: null,
          overrideLevel: null,
        },
      ],
      syntheses: null,
    };
    const incoming = {
      opacities: [
        { domainPattern: "keep", originPattern: null },
        { domainPattern: "new", originPattern: null },
      ],
      projections: null,
      syntheses: [
        {
          name: "team",
          domainPattern: null,
          originPattern: null,
          description: null,
          level: null,
        },
      ],
    };
    const diff = diffBoundaries(current, incoming);
    expect(diff.isNoOp).toBe(false);
    expect(diff.addedCount).toBe(2);
    expect(diff.removedCount).toBe(1);

    expect(diff.opacities.map((e) => e.status)).toEqual(["same", "added"]);
    expect(diff.projections.map((e) => e.status)).toEqual(["removed"]);
    expect(diff.syntheses.map((e) => e.status)).toEqual(["added"]);
  });

  it("treats a missing current boundary as all-added", () => {
    const diff = diffBoundaries(null, {
      opacities: [{ domainPattern: "new", originPattern: null }],
      projections: null,
      syntheses: null,
    });
    expect(diff.addedCount).toBe(1);
    expect(diff.removedCount).toBe(0);
    expect(diff.isNoOp).toBe(false);
  });

  it("treats an empty incoming boundary as all-removed", () => {
    const diff = diffBoundaries(
      {
        opacities: [{ domainPattern: "gone", originPattern: null }],
        projections: null,
        syntheses: null,
      },
      {
        opacities: null,
        projections: null,
        syntheses: null,
      },
    );
    expect(diff.addedCount).toBe(0);
    expect(diff.removedCount).toBe(1);
  });

  it("returns a no-op when both sides are empty", () => {
    const diff = diffBoundaries(null, {
      opacities: null,
      projections: null,
      syntheses: null,
    });
    expect(diff.isNoOp).toBe(true);
  });
});
