import { describe, expect, it } from "vitest";

import {
  canonicalUnitId,
  parseMultiParentInheritanceConflict,
} from "./multi-parent-conflict";

describe("parseMultiParentInheritanceConflict", () => {
  it("parses the structured 422 with `source` keys (UnitEndpoints shape)", () => {
    const body = {
      error: "MultiParentInheritanceConflict",
      conflictingFields: {
        runtime: [
          { source: "00000000000000000000000000000001", value: "claude-code" },
          { source: "00000000000000000000000000000002", value: "spring-voyage" },
        ],
      },
    };

    const parsed = parseMultiParentInheritanceConflict(body);
    expect(parsed).not.toBeNull();
    expect(parsed!.fields).toHaveLength(1);
    expect(parsed!.fields[0].field).toBe("runtime");
    expect(parsed!.fields[0].values).toEqual([
      { unitId: "00000000000000000000000000000001", value: "claude-code" },
      { unitId: "00000000000000000000000000000002", value: "spring-voyage" },
    ]);
  });

  it("parses the structured 422 with `unitId` keys (MultiParentInheritanceProblems shape)", () => {
    const body = {
      error: "MultiParentInheritanceConflict",
      conflictingFields: {
        "model.provider": [
          { unitId: "aaaa00000000000000000000000000aa", value: "anthropic" },
          { unitId: "bbbb00000000000000000000000000bb", value: "openai" },
        ],
      },
    };

    const parsed = parseMultiParentInheritanceConflict(body);
    expect(parsed).not.toBeNull();
    expect(parsed!.fields[0].values).toEqual([
      { unitId: "aaaa00000000000000000000000000aa", value: "anthropic" },
      { unitId: "bbbb00000000000000000000000000bb", value: "openai" },
    ]);
  });

  it("preserves multiple diverging fields in insertion order", () => {
    const body = {
      error: "MultiParentInheritanceConflict",
      conflictingFields: {
        runtime: [
          { source: "u1", value: "a" },
          { source: "u2", value: "b" },
        ],
        image: [
          { source: "u1", value: "img-a" },
          { source: "u2", value: "img-b" },
        ],
      },
    };

    const parsed = parseMultiParentInheritanceConflict(body);
    expect(parsed!.fields.map((f) => f.field)).toEqual(["runtime", "image"]);
  });

  it("re-parses string bodies that contain valid JSON", () => {
    const raw = JSON.stringify({
      error: "MultiParentInheritanceConflict",
      conflictingFields: {
        runtime: [
          { source: "u1", value: "a" },
          { source: "u2", value: "b" },
        ],
      },
    });

    const parsed = parseMultiParentInheritanceConflict(raw);
    expect(parsed).not.toBeNull();
    expect(parsed!.fields[0].field).toBe("runtime");
  });

  it("returns null when `error` does not match the discriminator", () => {
    expect(
      parseMultiParentInheritanceConflict({
        error: "SomethingElse",
        conflictingFields: { runtime: [] },
      }),
    ).toBeNull();
  });

  it("returns null when `conflictingFields` is missing", () => {
    expect(
      parseMultiParentInheritanceConflict({
        error: "MultiParentInheritanceConflict",
      }),
    ).toBeNull();
  });

  it("returns null when every field's value list is empty", () => {
    expect(
      parseMultiParentInheritanceConflict({
        error: "MultiParentInheritanceConflict",
        conflictingFields: { runtime: [] },
      }),
    ).toBeNull();
  });

  it("returns null for non-object inputs", () => {
    expect(parseMultiParentInheritanceConflict(null)).toBeNull();
    expect(parseMultiParentInheritanceConflict(undefined)).toBeNull();
    expect(parseMultiParentInheritanceConflict(42)).toBeNull();
    expect(parseMultiParentInheritanceConflict("not json")).toBeNull();
  });

  it("skips malformed parent entries but keeps well-formed ones", () => {
    const body = {
      error: "MultiParentInheritanceConflict",
      conflictingFields: {
        runtime: [
          { source: "u1", value: "claude-code" },
          { source: 42, value: "bad" }, // wrong unitId type — skipped
          { value: "no-source" },        // missing key — skipped
          { source: "u2", value: "spring-voyage" },
        ],
      },
    };
    const parsed = parseMultiParentInheritanceConflict(body);
    expect(parsed!.fields[0].values).toEqual([
      { unitId: "u1", value: "claude-code" },
      { unitId: "u2", value: "spring-voyage" },
    ]);
  });
});

describe("canonicalUnitId", () => {
  it("strips dashes and lowercases hyphenated GUIDs", () => {
    expect(canonicalUnitId("00000000-0000-0000-0000-000000000001")).toBe(
      "00000000000000000000000000000001",
    );
    expect(canonicalUnitId("AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE")).toBe(
      "aaaaaaaabbbbccccddddeeeeeeeeeeee",
    );
  });

  it("passes through already-canonical 32-hex strings", () => {
    expect(canonicalUnitId("aaaa00000000000000000000000000aa")).toBe(
      "aaaa00000000000000000000000000aa",
    );
  });

  it("returns null for inputs that are not 32 hex characters", () => {
    expect(canonicalUnitId("not-a-guid")).toBeNull();
    expect(canonicalUnitId("")).toBeNull();
    expect(canonicalUnitId("zzzz0000000000000000000000000000")).toBeNull();
  });
});
